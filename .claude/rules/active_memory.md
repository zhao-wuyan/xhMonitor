# Active Memory - Project Context

> Auto-generated understanding of frequently accessed files using QWEN.
> Last updated: 2026-01-06T04:46:03.724Z
> Files analyzed: 10
> CLI Tool: qwen

---

## SettingsWindow.xaml

WPF XAML file defining the UI for the settings window with dark theme styling.

- **Purpose**: Provides a user interface for configuring application settings including appearance, data collection, and system settings
- **Key Exports**: `SettingsWindow` class (via code-behind)
- **Dependencies**: 
  - `SettingsViewModel` (as DataContext)
  - WPF framework (PresentationFramework)
- **UI Features**:
  - Dark theme with #1E1E1E background and #E0E0E0 foreground
  - Tab-like navigation with icons (Palette, Chart, Settings, Info)
  - Custom styled controls (ComboBox, ScrollBar, ScrollViewer)
  - Organized sections: Appearance, Data Collection, System Settings
  - Process keywords management with add/delete functionality
  - Save/Cancel/Restore defaults buttons

## SettingsViewModel.cs

ViewModel implementing INotifyPropertyChanged for managing application settings.

- **Purpose**: Manages application settings state and provides API integration for loading/saving settings
- **Key Exports**: `SettingsViewModel` class with properties for all settings
- **Dependencies**: 
  - System.Net.Http for API communication
  - System.Text.Json for serialization
  - ObservableCollectionExtensions (for collection operations)
- **Key Properties**:
  - `ThemeColor` (string): UI theme ("Dark" default)
  - `Opacity` (int): Window opacity (60 default)
  - `ProcessKeywords` (ObservableCollection<string>): Keywords for process monitoring
  - `SystemInterval` (int): System metrics collection interval (1000ms default)
  - `ProcessInterval` (int): Process metrics collection interval (5000ms default)
  - `TopProcessCount` (int): Number of top processes to display (10 default)
  - `DataRetentionDays` (int): Days to retain data (30 default)
  - `StartWithWindows` (bool): Whether to start with Windows (false default)
  - `SignalRPort` (int): SignalR server port (35179 default)
  - `WebPort` (int): Web server port (35180 default)
  - `IsSaving` (bool): Flag indicating save operation status
- **Key Methods**:
  - `LoadSettingsAsync()`: Loads settings from API endpoint via HTTP GET
  - `SaveSettingsAsync()`: Saves settings to API endpoint via HTTP POST
  - `SetProperty<T>()`: INotifyPropertyChanged implementation helper

## SettingsWindow.xaml.cs

Code-behind for the settings window implementing window logic and event handling.

- **Purpose**: Handles window events, user interactions, and manages the SettingsViewModel
- **Key Exports**: `SettingsWindow` class inheriting from Window
- **Dependencies**:
  - `SettingsViewModel` (binds to UI via DataContext)
  - `InputDialog` (for adding process keywords)
- **Key Methods**:
  - `SettingsWindow()`: Constructor that initializes component and loads settings
  - `Save_Click()`: Handles save button click, calls ViewModel's SaveSettingsAsync
  - `Cancel_Click()`: Handles cancel button click, closes window with DialogResult=false
  - `RestoreDefaults_Click()`: Resets all settings to default values
  - `AddKeyword_Click()`: Opens input dialog to add new process keywords
  - `DeleteKeyword_Click()`: Removes selected process keyword from list
- **UI Controls**:
  - `KeywordsListBox`: Reference to the ListBox for process keywords (defined in XAML)

## ObservableCollectionExtensions.cs

Extension methods for ObservableCollection to provide additional functionality.

- **Purpose**: Provides utility methods to extend ObservableCollection capabilities
- **Key Exports**: Extension methods for ObservableCollection
- **Dependencies**: 
  - System.Collections.ObjectModel
  - System.Collections.Generic
  - System.Linq
- **Key Methods**:
  - `AddRange<T>()`: Adds multiple items to an ObservableCollection efficiently
  - `ReplaceAll<T>()`: Clears and replaces all items in an ObservableCollection with new items

## XhMonitor.Desktop.csproj

Project file defining the desktop application configuration and dependencies.

- **Purpose**: Defines the .NET 8 WPF desktop application project with dependencies and configuration
- **Key Exports**: Project configuration and package references
- **Dependencies**:
  - `Microsoft.AspNetCore.App` (FrameworkReference) - Provides ASP.NET Core features
  - `Microsoft.AspNetCore.SignalR.Client` (8.*) - For SignalR client functionality
  - `System.Text.Json` (8.*) - For JSON serialization/deserialization
- **Configuration**:
  - Target Framework: net8.0-windows
  - Output Type: WinExe
  - Uses WPF and Windows Forms
  - Nullable reference types enabled
  - Implicit Usings enabled
  - Version: 0.1.0

## File Relationships

- **SettingsWindow.xaml** defines the UI and binds to **SettingsViewModel** as its DataContext
- **SettingsWindow.xaml.cs** implements the window logic, instantiates **SettingsViewModel**, and handles UI events
- **SettingsViewModel** manages settings state and communicates with the backend API for loading/saving
- **ObservableCollectionExtensions** provides utility methods used by **SettingsViewModel** for managing collections (like ProcessKeywords)
- **XhMonitor.Desktop.csproj** defines the project and includes dependencies required by all components (System.Text.Json for serialization, etc.)
- The architecture follows MVVM pattern with clear separation between View (XAML), ViewModel (SettingsViewModel), and View code-behind (SettingsWindow.xaml.cs)

Analysis complete. I've examined all the requested files and provided a concise understanding of each in markdown format, including their purpose, key exports, dependencies, and relationships to other files.


---

