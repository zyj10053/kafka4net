﻿using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using kafka4net.Protocols;

namespace kafka4net
{
    internal class Connection
    {
        private static readonly ILogger _log = Logger.GetLogger();

        private readonly string _host;
        private readonly int _port;
        private readonly Action<Exception> _onError;
        private TcpClient _client;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private bool _closed;
        private Task _loopTask;
        private CancellationTokenSource _loopTaskCancel;

        internal ResponseCorrelation Correlation;

        internal Connection(string host, int port, Action<Exception> onError = null)
        {
            _host = host;
            _port = port;
            _onError = onError;
        }


        internal static Tuple<string, int>[] ParseAddress(string seedConnections)
        {
            return seedConnections.Split(',').
                Select(_ => _.Trim()).
                Where(_ => _ != null).
                Select(s =>
                {
                    int port = 9092;
                    string host = null;
                    if (s.Contains(':'))
                    {
                        var parts = s.Split(new[] { ":" }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2)
                        {
                            host = parts[0];
                            port = int.Parse(parts[1]);
                        }
                    }
                    else
                    {
                        host = s;
                    }
                    return Tuple.Create(host, port);
                }).ToArray();
        }

        internal async Task<TcpClient> GetClientAsync()
        {
            if (_closed)
                throw new ApplicationException("Attempt to reuse connection which is not supposed to be used anymore");

            await _connectionLock.WaitAsync();

            try
            {
                if (_client != null && !_client.Connected)
                {
                    _log.Debug("Replacing closed connection {0}:{1} with a new one", _host, _port);
                    _client = null;
                }

                if (_client == null)
                {

                    _client = new TcpClient();
                    _log.Debug("Opening new connection {0}:{1} with hash {2}", _host, _port, _client.GetHashCode());
                    await _client.ConnectAsync(_host, _port);

                    var currentClient = _client;
                    Correlation = new ResponseCorrelation(async e => 
                    {
                        await MarkSocketAsFailed(currentClient);
                        _onError(e);
                    }, _host+":"+_port+" conn object hash: " + GetHashCode());
                    
                    // If there wasa a prior connection and loop task, cancel them before creating a new one.
                    if (_loopTask != null && _loopTaskCancel != null)
                    {
                        _loopTaskCancel.Cancel();
                        _loopTask = null;
                    }

                    _loopTaskCancel = new CancellationTokenSource();

                    // Close connection in case of any exception. It is important, because in case of deserialization exception,
                    // we are out of sync and can't continue.
                    _loopTask = Correlation.CorrelateResponseLoop(_client, _loopTaskCancel.Token)
                        .ContinueWith(async t => 
                        {
                            _log.Debug("CorrelationLoop completed with status {0}. {1}", t.Status, t.Exception==null?"": string.Format("Closing connection because of error. {0}",t.Exception.Message));
                            await _connectionLock.WaitAsync();//.ConfigureAwait(false);
                            try
                            {
                                if (_client != null)
                                    _client.Close();
                            }
                            // ReSharper disable once EmptyGeneralCatchClause
                            catch {}
                            finally 
                            { 
                                _client = null;
                                _connectionLock.Release();
                            }
                        });

                    return _client;
                }
            }
            catch(Exception e)
            {
                // some exception getting a connection, clear what we have for next time.
                _client = null;
                if (_onError != null)
                    _onError(e);
                throw;
            }
            finally
            {
                _connectionLock.Release();
            }

            return _client;
        }

        public override string ToString()
        {
            return string.Format("Connection: {0}:{1}", _host, _port);
        }

        async Task MarkSocketAsFailed(TcpClient tcp)
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (_client != tcp)
                    return;

                _log.Debug("Marking connection as failed. {0}", this);

                if (_client != null)
                    try
                    {
                        if (_loopTaskCancel != null)
                            _loopTaskCancel.Cancel();
                        _client.Close();
                    }
                    // ReSharper disable once EmptyGeneralCatchClause
                    catch { /*empty*/ }
                _client = null;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task ShutdownAsync()
        {
            _closed = true;
            _log.Debug("{0} Shutting down.", this);
            try
            {
                if (_loopTaskCancel != null)
                    _loopTaskCancel.Cancel();

                if (_loopTask != null)
                {
                    try
                    {
                        _client.Close();
                        await _loopTask;
                        _log.Debug("Loop task completed");
                    }
                    catch (TaskCanceledException){}
                }
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch { }
            _log.Debug("{0} closed.", this);
        }
    }
}
