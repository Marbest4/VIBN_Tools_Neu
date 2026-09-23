using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using VIBN_Tools.Application;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.InterfaceOperation;
using VIBN_Tools.Settings;

namespace VIBN_Tools.Application.VM
{
    /// <summary>
    /// Coordinates selection, filtering and explicit connection/merge actions
    /// for FEE interfaces and their signals. The implementation details reside
    /// in <c>InterfaceOperationService</c>.
    /// </summary>
    public class InterfaceOperationPageVM : MvvmBase
    {

        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        ///                                                                                                                      ///   
        ///     C O N S T R U C T O R                                                                                            ///
        ///                                                                                                                      ///
        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////


        public InterfaceOperationPageVM()
        {

            _interfaceOperationService = new InterfaceOperationService();


            // Interface Connect
            AllInterfaces = new ObservableCollection<FeeInterface>();

            Interface1 = new InterfaceConnectViewModel();
            Interface2 = new InterfaceConnectViewModel();

            InterfacesView1 = new ListCollectionView(AllInterfaces);
            InterfacesView1.Filter = FilterSelectionInterface1;

            InterfacesView2 = new ListCollectionView(AllInterfaces);
            InterfacesView2.Filter = FilterSelectionInterface2;


            Interface1.PropertyChanged += OnInterfaceViewModelPropertyChanged;
            Interface2.PropertyChanged += OnInterfaceViewModelPropertyChanged;

            InterfaceConnectionModes = new ObservableCollection<InterfaceConnectMode>(Enum.GetValues(typeof(InterfaceConnectMode)).Cast<InterfaceConnectMode>());



            // Load Signals if already data existing
            //if (Services.FeeObjects.AllFeeObjects?.Any() == true)
            //{
            //    GetAllInterfaces(this, new FeeObjectsUpdatedEventargs());
            //}

            Services.FeeObjects.FeeObjectsUpdated += OnFeeObjectsUpdated;





            // Interface Merge
            AllSignals = new ObservableCollection<FeeInterfaceSignal>();

            // Load Signals if already data existing
            if (Services.FeeObjects.AllFeeObjects?.Any() == true)
            {
                GetAllSignals(this, new FeeObjectsUpdatedEventargs());
            }

            Services.FeeObjects.FeeObjectsUpdated += GetAllSignals;

            SignalsView = CollectionViewSource.GetDefaultView(AllSignals);
            SignalsView.Filter = FilterSignals;

            _filterDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMicroseconds(300), };
            _filterDebounceTimer.Tick += (s, e) =>
            {
                _filterDebounceTimer.Stop();
                SignalsView.Refresh();
            };

            Connection.PropertyChanged += OnFeeConnectionPropertyChanged;


        }



        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        ///                                                                                                                      ///   
        ///     G E N E R A L                                                                                                    ///
        ///                                                                                                                      ///
        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////


        private readonly InterfaceOperationService _interfaceOperationService;

        public FeeConnectionService Connection => Services.Connection;









        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        ///                                                                                                                      ///   
        ///     I N T E R F A C E   C O N N E C T O R                                                                            ///
        ///                                                                                                                      ///
        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////


        //===========================================================================================================================
        // G E N E R A L   P R O P E R T I E S
        //===========================================================================================================================

        public InterfaceConnectViewModel Interface1 { get; set; }
        public InterfaceConnectViewModel Interface2 { get; set; }

        public ObservableCollection<FeeInterface> AllInterfaces { get; set; }

        public ICollectionView InterfacesView1 { get; set; }
        public ICollectionView InterfacesView2 { get; set; }

        //public ObservableCollection<FeeInterface> AvailableInterfacesForSelection1 { get; set; }
        //public ObservableCollection<FeeInterface> AvailableInterfacesForSelection2 { get; set; }

        public ObservableCollection<InterfaceConnectMode> InterfaceConnectionModes { get; set; }



        public ICommand ReloadFeeInterfaces => GetCommandBindingAsync(Reload_FeeInterfaces);

        public ICommand ConnectInterfaces => GetCommandBindingAsync(Connect_Interfaces);


        private bool _isUpdatingMode = false;
        private bool _isUpdatingByteCount = false;


        private bool _isBusyReloadInterfaces;
        public bool IsBusyReloadInterfaces
        {
            get { return _isBusyReloadInterfaces; }
            set 
            { 
                _isBusyReloadInterfaces = value;
                OnPropertyChanged();
            }
        }


        public bool CanConnetInterfaces => Connection.CanUseFeeFeatures && IsInterfaceValid(Interface1) && IsInterfaceValid(Interface2);

        public bool CanReloadFeeData => Connection.CanUseFeeFeatures;

        private void OnFeeConnectionPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName is nameof(FeeConnectionService.IsConnected) or
                nameof(FeeConnectionService.CanUseFeeFeatures))
            {
                OnPropertyChanged(nameof(CanConnetInterfaces));
                OnPropertyChanged(nameof(CanReloadFeeData));
            }
        }









        //===========================================================================================================================
        // M E T H O D S   ( B U T T O N S )
        //===========================================================================================================================


        public async Task Reload_FeeInterfaces()
        {
            //await Services.FeeObjects.UpdateFeeDataAsync();

            await LoadInterfacesAsync();
        }


        public async Task Connect_Interfaces()
        {
            if (!EnsureFeeConnection(nameof(ConnectInterfaces)))
                return;

            await _interfaceOperationService.ConnectInterfacesAsync(Interface1, Interface2);
        }




        //===========================================================================================================================
        // M E T H O D S   ( H E L P E R S )
        //===========================================================================================================================

        private async Task LoadInterfacesAsync()
        {
            if (!EnsureFeeConnection(nameof(ReloadFeeInterfaces)))
                return;

            IsBusyReloadInterfaces = true;
            try
            {
                AllInterfaces.Clear();
                var interfaces = await FeeInterface.GetAllInterfacesAsync();
                foreach (var item in interfaces)
                    AllInterfaces.Add(item);
                InterfacesView1.Refresh();
                InterfacesView2.Refresh();
            }
            finally
            {
                IsBusyReloadInterfaces = false;
            }
        }



        private void OnInterfaceViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Change on SelectedInterface
            if (e.PropertyName == nameof(InterfaceConnectViewModel.SelectedInterface))
            {
                if (sender == Interface1)
                    InterfacesView2.Refresh();
                else if(sender == Interface2)
                    InterfacesView1.Refresh();

                return;
            }

            // Update Execute Button
            if(e.PropertyName == nameof(InterfaceConnectViewModel.SelectedInterface) ||
                e.PropertyName == nameof(InterfaceConnectViewModel.ByteCount))
            {
                OnPropertyChanged(nameof(CanConnetInterfaces));
            }


            // Change on SelectedMode
            if(e.PropertyName == nameof(InterfaceConnectViewModel.SelectedMode))
            {
                // lock infinite loops
                if (_isUpdatingMode) return;
                _isUpdatingMode = true;

                if(sender == Interface1)
                {
                    Interface2.SelectedMode = GetOppositeMode(Interface1.SelectedMode);
                }                   
                else if(sender == Interface2)
                {
                    Interface1.SelectedMode = GetOppositeMode(Interface2.SelectedMode);
                }

                _isUpdatingMode = false;
                return;                  
            }

            // Change on ByteCount
            if(e.PropertyName == nameof(InterfaceConnectViewModel.ByteCount))
            {
                if (_isUpdatingByteCount) return;
                _isUpdatingByteCount = true;

                if (sender == Interface1)
                {
                    Interface2.ByteCount = Interface1.ByteCount;
                }
                else if (sender == Interface2)
                {
                    Interface1.ByteCount = Interface2.ByteCount;
                }

                _isUpdatingByteCount = false;
                return;
            }


        }


        private bool FilterSelectionInterface1(object obj)
        {
            if (obj is not FeeInterface iface)
                return false;

            return Interface2?.SelectedInterface == null || iface != Interface2.SelectedInterface;
        }

        private bool FilterSelectionInterface2(object obj)
        {
            if (obj is not FeeInterface iface)
                return false;

            return Interface1?.SelectedInterface == null || iface != Interface1.SelectedInterface;
        }





        private InterfaceConnectMode GetOppositeMode(InterfaceConnectMode mode)
        {
            return mode switch
            {
                InterfaceConnectMode.SendOnly => InterfaceConnectMode.ReceiveOnly,
                InterfaceConnectMode.ReceiveOnly => InterfaceConnectMode.SendOnly,
                InterfaceConnectMode.SendReceive => InterfaceConnectMode.SendReceive,
                _ => InterfaceConnectMode.SendReceive,
            };
        }









        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        ///                                                                                                                      ///   
        ///     I N T E R F A C E   M E R G E                                                                                    ///
        ///                                                                                                                      ///
        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////


        //===========================================================================================================================
        // F I L T E R
        //===========================================================================================================================

        private string _textFilterSignals;
        public string TextFilterSignals
        {
            get 
            { 
                return _textFilterSignals; 
            }
            set 
            { 
                _textFilterSignals = value;

                // Start Debounce
                _filterDebounceTimer.Stop();
                _filterDebounceTimer.Start();
            }
        }


        public ObservableCollection<FeeInterfaceSignal> AllSignals { get; set; }

        public ICollectionView SignalsView { get; set; }

        private readonly DispatcherTimer _filterDebounceTimer;



        public ICommand SelectVisibleSignals => GetCommandBindingAsync(Select_VisibleSignals);
        public ICommand DeselectVisibleSignals => GetCommandBindingAsync(Deselect_VisibleSignals);

        public ICommand ReloadFeeSignals => GetCommandBindingAsync(Reload_FeeSignals);

        public ICommand MergeSignalsToInterface => GetCommandBindingAsync(MergeSignals_ToInterface);



        private bool _isBusyUpdatingFeeData;
        public bool IsBusyUpdatingFeeData
        {
            get { return _isBusyUpdatingFeeData; }
            set 
            { 
                _isBusyUpdatingFeeData = value;
                OnPropertyChanged();
            }
        }


       



        //===========================================================================================================================
        // M E T H O D S   ( B U T T O N S )
        //===========================================================================================================================

        public async Task Select_VisibleSignals()
        {
            foreach (FeeInterfaceSignal s in SignalsView.Cast<FeeInterfaceSignal>())
                s.IsSelected = true;
        }


        public async Task Deselect_VisibleSignals()
        {
            foreach (FeeInterfaceSignal s in SignalsView.Cast<FeeInterfaceSignal>())
                s.IsSelected = false;
        }


        public async Task Reload_FeeSignals()
        {
            if (!EnsureFeeConnection(nameof(ReloadFeeSignals)))
                return;

            IsBusyUpdatingFeeData = true;
            try
            {
                await Services.FeeObjects.UpdateFeeDataAsync();
            }
            finally
            {
                IsBusyUpdatingFeeData = false;
            }
        }


        public async Task MergeSignals_ToInterface()
        {
            if (!EnsureFeeConnection(nameof(MergeSignalsToInterface)))
                return;

            var selectedSignals = AllSignals.Where(x => x.IsSelected).ToList();

            await _interfaceOperationService.MergeSignalsAsync(selectedSignals);

            await Reload_FeeSignals();
        }

        private bool EnsureFeeConnection(string operation)
        {
            if (Connection.CanUseFeeFeatures)
                return true;

            ApplicationLogService.Instance.Warning(
                "Interface Operation",
                $"{operation} wurde nicht ausgeführt: {FeeConnectionService.MissingConnectionMessage}");
            return false;
        }





        //===========================================================================================================================
        // M E T H O D S   ( H E L P E R S )
        //===========================================================================================================================

        private void GetAllSignals(object sender, FeeObjectsUpdatedEventargs e)
        {
            AllSignals.Clear();

            var signalsList = Services.FeeObjects.AllFeeObjects.OfType<FeeInterface>().SelectMany(x => x.Signals).ToList();

            foreach (var item in signalsList)
            {
                AllSignals.Add(item);
            }

            SignalsView.Refresh();
        }




        private bool FilterSignals(object obj)
        {
            if (obj is not FeeInterfaceSignal signal)
                return false;

            if (string.IsNullOrWhiteSpace(TextFilterSignals))
                return true;

            string filter = TextFilterSignals.Trim();

            return
                (signal.Tag?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (signal.Address?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (signal.Comment?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (signal.IOTypeString?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (signal.UsageString?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (signal.ParentInterface?.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
        }


        private void OnFeeObjectsUpdated(object sender, FeeObjectsUpdatedEventargs e)
        {
            IsBusyUpdatingFeeData = false;
        }





        private bool IsInterfaceValid(InterfaceConnectViewModel vm)
        {
            if (vm.SelectedInterface == null) return false;

            if(vm.ByteCount <= 0) return false;

            return true;
        }






    }
}
