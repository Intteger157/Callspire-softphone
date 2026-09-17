using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Softphone.AppHost;
using Softphone.AppHost.ViewModels;
using Softphone.Service.Contracts;
using Softphone.Service.Ipc;

namespace Softphone.Service
{
    /// <summary>
    /// Replaces XAML data binding: observes <see cref="MainViewModel"/> (and its nested view models and
    /// collections) and pushes a coalesced <c>stateSnapshot</c> to Swift. Snapshots are full — the state
    /// is small (a few strings, ≤ a few hundred history rows) and a full snapshot keeps the Swift side
    /// trivially consistent after reconnects.
    /// </summary>
    public sealed class StateProjector : IDisposable
    {
        private readonly IpcServer _ipc;
        private readonly ServiceDispatcher _dispatcher;
        private readonly DesktopAppController _controller;
        private readonly CallSessionHost _calls;
        private int _pending;
        private bool _disposed;

        public StateProjector(IpcServer ipc, ServiceDispatcher dispatcher, DesktopAppController controller, CallSessionHost calls)
        {
            _ipc = ipc;
            _dispatcher = dispatcher;
            _controller = controller;
            _calls = calls;

            var vm = controller.ViewModel;
            vm.PropertyChanged += OnChanged;
            vm.Main.PropertyChanged += OnChanged;
            vm.Secondary.PropertyChanged += OnChanged;
            vm.Statistics.PropertyChanged += OnChanged;
            vm.History.CollectionChanged += OnCollectionChanged;
            vm.CallerIds.CollectionChanged += OnCollectionChanged;
            vm.Statistics.Daily.CollectionChanged += OnCollectionChanged;
            vm.Statistics.Hourly.CollectionChanged += OnCollectionChanged;
            vm.Statistics.ConnectionCompare.CollectionChanged += OnCollectionChanged;
            vm.Statistics.CallerIds.CollectionChanged += OnCollectionChanged;
            vm.Statistics.TopNumbers.CollectionChanged += OnCollectionChanged;
            vm.Statistics.ConnectionOptions.CollectionChanged += OnCollectionChanged;
            calls.ActiveCallChanged += Schedule;
            ThemePreferences.ConfiguredModeChanged += _ => Schedule();
        }

        private void OnChanged(object? sender, PropertyChangedEventArgs e) => Schedule();
        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Schedule();

        /// <summary>Coalesces bursts (history replace = N collection events) into one push ~40 ms later.</summary>
        public void Schedule()
        {
            if (_disposed || Interlocked.Exchange(ref _pending, 1) == 1) return;
            _ = Task.Delay(40).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _pending, 0);
                _dispatcher.Post(() => _ = _ipc.SendEventAsync("stateSnapshot", Build()));
            }, TaskScheduler.Default);
        }

        /// <summary>Must run on the dispatcher thread.</summary>
        public MainStateDto Build()
            => MainStateDto.From(_controller.ViewModel, _calls.HasActiveCall, ThemePreferences.GetConfiguredMode().ToString().ToLowerInvariant());

        public Task<MainStateDto> BuildAsync() => _dispatcher.InvokeAsync(Build);

        public void Dispose() { _disposed = true; }
    }
}
