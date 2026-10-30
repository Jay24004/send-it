using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using SendIt.App.ViewModels;
using SendIt.Core.Models;

namespace SendIt.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Cleanup();
            }
        };
    }

    // =========================================================================
    // WINDOW TITLEBAR CONTROLS
    // =========================================================================

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // =========================================================================
    // DRAG AND DROP
    // =========================================================================

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && DataContext is MainViewModel vm)
            {
                e.Handled = true;
                await vm.HandleDroppedFilesAsync(null, files);
            }
        }
    }

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && DataContext is MainViewModel vm)
            {
                e.Handled = true;
                await vm.HandleDroppedFilesAsync(null, files);
            }
        }
    }

    private void OnDropZoneMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // If clicking on empty space inside drop zone, open file browser
        if (e.OriginalSource is FrameworkElement fe && (fe is System.Windows.Shapes.Rectangle || fe is System.Windows.Controls.Grid || fe is System.Windows.Controls.Border))
        {
            if (DataContext is MainViewModel vm)
            {
                vm.BrowseAndStageFiles();
            }
        }
    }

    private async void OnPeerCardDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && DataContext is MainViewModel vm)
            {
                if (sender is FrameworkElement elem)
                {
                    DiscoveredPeer? targetPeer = elem.Tag switch
                    {
                        SelectablePeerViewModel sp => sp.Peer,
                        DiscoveredPeer dp => dp,
                        _ => null
                    };

                    if (targetPeer != null)
                    {
                        e.Handled = true;
                        await vm.HandleDroppedFilesAsync(targetPeer, files);
                    }
                }
            }
        }
    }
}
