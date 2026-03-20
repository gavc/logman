using System;
using System.Runtime.Versioning;

namespace LogMan.ViewModels
{
    [SupportedOSPlatform("windows")]
    public class LogSourceViewModel : ViewModelBase
    {
        public string Name
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string MachineName
        {
            get;
            set => SetProperty(ref field, value);
        } = "Local";

        public string DisplayName => string.Equals(MachineName, "Local", StringComparison.OrdinalIgnoreCase) 
            ? Name 
            : $"{MachineName} - {Name}";

        public bool IsSelected
        {
            get;
            set => SetProperty(ref field, value);
        }
    }
}
