﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Streaming.Payloads;
using Microsoft.Bot.Streaming.Transport;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Microsoft.Bot.Connector.Streaming.Transport
{
    internal class TransportHandler : IObservable<(Header Header, ReadOnlySequence<byte> Payload)>, IDisposable
    {
        private readonly IDuplexPipe _transport;
        private readonly ILogger _logger;

        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1);
        private readonly TimeSpan _semaphoreTimeout = TimeSpan.FromSeconds(10);
        private readonly byte[] _sendHeaderBuffer = new byte[TransportConstants.MaxHeaderLength];

        private IObserver<(Header, ReadOnlySequence<byte>)> _observer;
        private bool _disposedValue;

        public TransportHandler(IDuplexPipe transport, ILogger logger)
        {
            _transport = transport;
            _logger = logger;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "We want to catch all exceptions in the message loop.")]
        public async Task ListenAsync(CancellationToken cancellationToken)
        {
            var input = _transport.Input;
            bool aborted = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result;

                result = await input.ReadAsync().ConfigureAwait(false);

                var buffer = result.Buffer;

                try
                {
                    if (result.IsCanceled)
                    {
                        break;
                    }

                    if (!buffer.IsEmpty)
                    {
                        while (TryParseHeader(ref buffer, out Header header))
                        {
                            Log.PayloadReceived(_logger, header);

                            ReadOnlySequence<byte> payload = ReadOnlySequence<byte>.Empty;

                            if (header.PayloadLength > 0)
                            {
                                if (buffer.Length < header.PayloadLength)
                                {
                                    input.AdvanceTo(buffer.Start, buffer.End);

                                    result = await input.ReadAsync().ConfigureAwait(false);

                                    if (result.IsCanceled)
                                    {
                                        break;
                                    }

                                    buffer = result.Buffer;
                                }

                                if (buffer.Length >= header.PayloadLength)
                                {
                                    payload = buffer.Slice(buffer.Start, header.PayloadLength);
                                    buffer = buffer.Slice(header.PayloadLength);
                                }
                                else
                                {
                                    break;
                                }
                            }

                            _observer.OnNext((header, payload));
                        }
                    }

                    if (result.IsCompleted)
                    {
                        if (buffer.IsEmpty)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Don't treat OperationCanceledException as an error, it's basically a "control flow"
                    // exception to stop things from running.
                }
                catch (Exception ex)
                {
                    Log.ReadFrameFailed(_logger, ex);

                    // This failure means we are tearing down the connection, so return and let the cancellation 
                    // and draining take place.
                    await input.CompleteAsync(ex).ConfigureAwait(false);

                    aborted = true;
                        
                    return;
                }
                finally
                {
                    if (!aborted)
                    {
                        input.AdvanceTo(buffer.Start, buffer.End);
                    }
                }
            }

            await input.CompleteAsync().ConfigureAwait(false);

            await _transport.Output.CompleteAsync().ConfigureAwait(false);
        }

        public Task StopAsync()
        {
            _transport.Input.CancelPendingRead();
            return Task.CompletedTask;
        }

        public virtual async Task SendResponseAsync(Guid id, ResponsePayload response, CancellationToken cancellationToken = default)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var responseBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(response));

            var responseHeader = new Header()
            {
                Type = PayloadTypes.Response,
                Id = id,
                PayloadLength = (int)responseBytes.Length,
                End = true,
            };

            await WriteAsync(
                header: responseHeader,
                writeFunc: async pipeWriter => await pipeWriter.WriteAsync(responseBytes).ConfigureAwait(false)).ConfigureAwait(false);
        }

        public virtual async Task SendRequestAsync(Guid id, RequestPayload request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var requestBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(request));

            var requestHeader = new Header()
            {
                Type = PayloadTypes.Request,
                Id = id,
                PayloadLength = (int)requestBytes.Length,
                End = true,
            };

            await WriteAsync(
                header: requestHeader, 
                writeFunc: async pipeWriter => await pipeWriter.WriteAsync(requestBytes).ConfigureAwait(false)).ConfigureAwait(false);
        }

        public virtual async Task SendStreamAsync(Guid id, Stream stream, CancellationToken cancellationToken)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var streamHeader = new Header()
            {
                Type = PayloadTypes.Stream,
                Id = id,
                PayloadLength = (int)stream.Length,
                End = true,
            };

            await WriteAsync(streamHeader, pipeWriter => stream.CopyToAsync(pipeWriter)).ConfigureAwait(false);
        }

        public IDisposable Subscribe(IObserver<(Header, ReadOnlySequence<byte>)> observer)
        {
            if (_observer != null)
            {
                throw new InvalidOperationException("The protocol expects only a single observer.");
            }

            _observer = observer ?? throw new ArgumentNullException(nameof(observer));

            return null;
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _writeLock?.Dispose();
                }

                _disposedValue = true;
            }
        }

        private static bool TryParseHeader(ref ReadOnlySequence<byte> buffer, out Header header)
        {
            if (buffer.IsEmpty)
            {
                header = null;
                return false;
            }

            var headerBuffer = buffer.Slice(0, Math.Min(TransportConstants.MaxHeaderLength, buffer.Length));

            // Optimization opportunity: instead of headerBuffer.ToArray() which does a 48 byte heap allocation,
            // do a best effort attempt to use MemoryMashal.TryGetArray. Since it has a lot of corner cases, 
            // keeping it simple for now and we can optimize further if data says we required it.
            header = HeaderSerializer.Deserialize(headerBuffer.ToArray(), 0, TransportConstants.MaxHeaderLength);

            buffer = buffer.Slice(TransportConstants.MaxHeaderLength);

            return true;
        }

        private async Task WriteAsync(Header header, Func<PipeWriter, Task> writeFunc, CancellationToken cancellationToken = default)
        {
            if (await _writeLock.WaitAsync(_semaphoreTimeout, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    HeaderSerializer.Serialize(header, _sendHeaderBuffer, 0);

                    Log.SendingPayload(_logger, header);

                    var output = _transport.Output;

                    await output.WriteAsync(_sendHeaderBuffer).ConfigureAwait(false);
                    await writeFunc(output).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            else
            {
                Log.SemaphoreTimeOut(_logger, header);
            }
        }

        private static class Log
        {
            private static readonly Action<ILogger, Header, Exception> _payloadReceived =
                LoggerMessage.Define<Header>(LogLevel.Debug, new EventId(1, nameof(PayloadReceived)), "Payload received. Header: {Header}.");

            private static readonly Action<ILogger, Exception> _readFrameFailed =
                LoggerMessage.Define(LogLevel.Error, new EventId(2, nameof(ReadFrameFailed)), "Failed to read frame from transport.");

            private static readonly Action<ILogger, Header, Exception> _payloadSending =
                LoggerMessage.Define<Header>(LogLevel.Debug, new EventId(3, nameof(SendingPayload)), "Sending Payload. Header: {Header}.");

            private static readonly Action<ILogger, Header, Exception> _semaphoreTimeOut =
                LoggerMessage.Define<Header>(LogLevel.Error, new EventId(4, nameof(SemaphoreTimeOut)), "Timed out trying to acquire write semaphore. Header: {Header}.");

            public static void PayloadReceived(ILogger logger, Header header) => _payloadReceived(logger, header, null);

            public static void ReadFrameFailed(ILogger logger, Exception ex) => _readFrameFailed(logger, ex);

            public static void SendingPayload(ILogger logger, Header header) => _payloadSending(logger, header, null);

            public static void SemaphoreTimeOut(ILogger logger, Header header) => _semaphoreTimeOut(logger, header, null);
        }
    }
}