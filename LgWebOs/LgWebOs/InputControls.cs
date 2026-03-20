using System;
using Guss.Communications.Sockets;
using Guss.Communications.ModuleFramework.Logging;

namespace LgWebOs
{
    internal class InputControls : IDisposable
    {
        private bool _disposed;
        private readonly WebSocketClient _socketClient;

        internal bool IsConnected
        {
            get
            {
                if (_socketClient == null) return false;

                return _socketClient.IsConnected;
            }
        }

        internal InputControls(string ipAddress, ushort port, string path, ILogger logger)
        {
            _socketClient = new WebSocketClient(new Logger("LgWebOs -- Input Controls")
            {
                DebugLevel = logger.DebugLevel
            });

            path = path.Replace("wss:", string.Empty);
            path = path.Replace("ws:", string.Empty);
            _socketClient.Connect("wss://" + ipAddress + path, port);
        }

        internal void SendKey(string key)
        {
            key = string.Format("type:button\nname:{0}\n\n", key.ToUpper());

            _socketClient.SendCommand(key);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            _disposed = true;

            if(disposing)
            {
                if(_socketClient != null)
                    _socketClient.Dispose();
            }
        }
    }
}