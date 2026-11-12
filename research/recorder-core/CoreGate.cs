using System;

namespace OrderFlowTools
{
    /// <summary>
    /// Finding 3 (Recheck): koordiniert Dispose und erstmalige Core-Erzeugung über DENSELBEN terminalen Zustand.
    /// Der ATAS-Indicator kann vor dem ersten Callback disposed werden; danach darf KEIN neuer, laufender Kern mehr
    /// entstehen. Diese Klasse ist ATAS-frei, damit genau dieser Adapterzustand offline testbar ist; der Indicator
    /// delegiert Core()/OnDispose an sie.
    /// </summary>
    public sealed class CoreGate
    {
        private readonly object _lock = new();
        private RecorderCore? _core;
        private bool _disposed;

        /// <summary>Liefert den (einmalig erzeugten, gestarteten) Kern — oder null, wenn bereits disposed (terminal).</summary>
        public RecorderCore? GetOrCreateAndStart(Func<RecorderCore> factory)
        {
            lock (_lock)
            {
                if (_disposed) return null;                 // terminal: nach Dispose kein neuer Running-Kern
                _core ??= factory();
                _core.Start();                              // idempotent
                return _core;
            }
        }

        /// <summary>Terminaler Abschluss: markiert disposed (verhindert spätere Erzeugung) und finalisiert den Kern.</summary>
        public void Dispose()
        {
            RecorderCore? c;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                c = _core;
            }
            try { c?.Finish(); } catch { }
        }

        public bool IsDisposed { get { lock (_lock) { return _disposed; } } }
        public RecorderCore? CoreOrNull { get { lock (_lock) { return _core; } } }
    }
}
