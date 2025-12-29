// File: Host.Win/Models/McpServerItem.cs
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Host.Win.Models
{
    /// <summary>
    /// UI-bindable wrapper for MCP server with active state tracking.
    /// </summary>
    public sealed class McpServerItem : INotifyPropertyChanged
    {
        private bool _isActive = true;

        // Friendly display names for known MCP servers
        private static readonly Dictionary<string, string> FriendlyNames = new()
        {
            ["windows_automation"] = "Windows",
            ["outlook_integration"] = "Microsoft Outlook",
            ["filesystem"] = "File System",
            ["browser"] = "Web Browser",
            ["calendar"] = "Calendar",
            ["email"] = "Email",
        };

        public string Name { get; }
        public string Description { get; }

        /// <summary>
        /// Friendly display name for the UI.
        /// </summary>
        public string DisplayName => FriendlyNames.TryGetValue(Name, out var friendly) ? friendly : Name;

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                OnPropertyChanged();
            }
        }

        public McpServerItem(string name, string description, bool isActive = true)
        {
            Name = name;
            Description = description;
            _isActive = isActive;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
