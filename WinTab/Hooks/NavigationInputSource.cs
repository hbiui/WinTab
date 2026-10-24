using System;
using System.Collections.Generic;
using System.Linq;

namespace WinTab.Hooks;

/// <summary>
/// Shares the process-wide Raw Input registration between navigation features. Each subscriber has an
/// independent lifetime; the native observer exists only while at least one feature is subscribed.
/// </summary>
internal sealed class NavigationInputSource : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly Func<Action<NavigationPointerInput>, Action, Action<nint>, IDisposable> _createObserver;
    private IDisposable? _observer;
    private bool _disposed;

    public NavigationInputSource()
        : this((pointer, cancel, foreground) => new NavigationInputObserver(pointer, cancel, foreground))
    {
    }

    internal NavigationInputSource(Func<Action<NavigationPointerInput>, Action, Action<nint>, IDisposable> createObserver) =>
        _createObserver = createObserver;

    internal IDisposable Subscribe(Action<NavigationPointerInput> pointer, Action cancel, Action<nint> foreground)
    {
        var subscription = new Subscription(this, pointer, cancel, foreground);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _observer ??= _createObserver(DispatchPointer, DispatchCancel, DispatchForeground);
            _subscriptions.Add(subscription);
        }
        return subscription;
    }

    private Subscription[] Snapshot()
    {
        lock (_gate)
            return _subscriptions.Where(subscription => subscription.Active).ToArray();
    }

    private void DispatchPointer(NavigationPointerInput input)
    {
        foreach (var subscription in Snapshot())
        {
            try { subscription.Pointer(input); }
            catch (Exception exception) { ExplorerDebugLog.Write($"Navigation pointer subscriber failed: {exception.Message}"); }
        }
    }

    private void DispatchCancel()
    {
        foreach (var subscription in Snapshot())
        {
            try { subscription.Cancel(); }
            catch (Exception exception) { ExplorerDebugLog.Write($"Navigation cancellation subscriber failed: {exception.Message}"); }
        }
    }

    private void DispatchForeground(nint window)
    {
        foreach (var subscription in Snapshot())
        {
            try { subscription.Foreground(window); }
            catch (Exception exception) { ExplorerDebugLog.Write($"Navigation foreground subscriber failed: {exception.Message}"); }
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        IDisposable? observer = null;
        lock (_gate)
        {
            if (!subscription.Active) return;
            subscription.Active = false;
            _subscriptions.Remove(subscription);
            if (_subscriptions.Count == 0)
            {
                observer = _observer;
                _observer = null;
            }
        }
        observer?.Dispose();
    }

    public void Dispose()
    {
        IDisposable? observer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var subscription in _subscriptions)
                subscription.Active = false;
            _subscriptions.Clear();
            observer = _observer;
            _observer = null;
        }
        observer?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class Subscription(
        NavigationInputSource owner,
        Action<NavigationPointerInput> pointer,
        Action cancel,
        Action<nint> foreground) : IDisposable
    {
        internal bool Active { get; set; } = true;
        internal Action<NavigationPointerInput> Pointer { get; } = pointer;
        internal Action Cancel { get; } = cancel;
        internal Action<nint> Foreground { get; } = foreground;
        public void Dispose() => owner.Unsubscribe(this);
    }
}
