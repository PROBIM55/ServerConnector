// Обёртка над Tekla.Structures.Model.Model. Владеет инстансом, живёт внутри
// TeklaWorker (single-thread). Не делает ничего параллельно — все вызовы
// идут с worker thread. Reconnect выполняется только при попытке использовать
// connection (lazy, см. EnsureConnected). См. plan §6.

#nullable enable

using System;
using Platform.Bridge.Desktop.Tekla.Logging;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.Tekla
{
    public sealed class TeklaConnection
    {
        private readonly JsonLineLogger _log;
        private Model? _model;
        private bool _wasConnected;

        public TeklaConnection(JsonLineLogger log)
        {
            _log = log;
        }

        /// <summary>True если в момент вызова Tekla connected И модель загружена.</summary>
        public bool IsConnected
        {
            get
            {
                if (_model is null) return false;
                try { return _model.GetConnectionStatus(); }
                catch { return false; }
            }
        }

        public string? ModelName
        {
            get
            {
                if (!IsConnected) return null;
                try { return _model!.GetInfo()?.ModelName; }
                catch { return null; }
            }
        }

        public string? ModelPath
        {
            get
            {
                if (!IsConnected) return null;
                try { return _model!.GetInfo()?.ModelPath; }
                catch { return null; }
            }
        }

        /// <summary>
        /// Принудительно сбросить состояние подключения — следующий <see cref="EnsureConnected"/>
        /// пересоздаст <c>Model</c>. Используется worker'ом при stale-handle
        /// после рестарта Tekla (<see cref="StaleTeklaDetection.IsStaleHandle"/>).
        /// </summary>
        public void Invalidate()
        {
            if (_model is not null)
            {
                _log.Warn("tekla.connection.invalidate");
                _model = null;
            }
            _wasConnected = false;
        }

        /// <summary>
        /// Гарантирует что _model подключена. Если был disconnect — пробует
        /// заново подключиться. Бросает <see cref="TeklaDisconnectedException"/>
        /// если Tekla не запущена / нет модели.
        /// </summary>
        public Model EnsureConnected()
        {
            if (_model is null)
            {
                _model = new Model();
            }
            if (!_model.GetConnectionStatus())
            {
                if (_wasConnected)
                {
                    _log.Warn("tekla.disconnected — attempting reconnect");
                }
                _model = new Model();
                if (!_model.GetConnectionStatus())
                {
                    _wasConnected = false;
                    throw new TeklaDisconnectedException();
                }
            }
            if (!_wasConnected)
            {
                _wasConnected = true;
                _log.Info("tekla.connected", new { model = ModelName, path = ModelPath });
            }
            return _model;
        }
    }

    public sealed class TeklaDisconnectedException : Exception
    {
        public TeklaDisconnectedException() : base("Tekla model is not open or Tekla is not running.") { }
    }

    /// <summary>
    /// Bridge.Desktop удерживал stale remote-references на старую Tekla session,
    /// внутренний reconnect не помог. HTTP-уровень маппит в 503/BRIDGE_STALE_TEKLA;
    /// Connector видит этот код и делает hard-restart Bridge.Desktop как fallback.
    /// </summary>
    public sealed class TeklaStaleHandleException : Exception
    {
        public TeklaStaleHandleException(Exception inner)
            : base("Tekla remote references became stale after Tekla restart; in-process reconnect failed.", inner) { }
    }
}
