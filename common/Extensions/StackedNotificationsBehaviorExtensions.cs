﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.Behaviors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace DevHome.Common.Extensions;

public static class StackedNotificationsBehaviorExtensions
{
    public static void ShowWithWindowExtension(this StackedNotificationsBehavior behavior, string title, string message, InfoBarSeverity severity, IRelayCommand? command = null, string? buttonContent = null)
    {
        var dispatcherQueue = Application.Current.GetService<WindowEx>().DispatcherQueue;
        var notificationToShow = new Notification
        {
            Title = title,
            Message = message,
            Severity = severity,
            ActionButton = new Button
            {
                Content = buttonContent,
                Command = command,
            },
        };

        // Make the command parameter the notification so RelayCommands can reference the notification incase they need
        // to close it within the command.
        notificationToShow.ActionButton.CommandParameter = notificationToShow;

        dispatcherQueue.TryEnqueue(() =>
        {
            behavior.Show(notificationToShow);
        });
    }

    public static void RemoveWithWindowExtension(this StackedNotificationsBehavior behavior, Notification notification)
    {
        var dispatcherQueue = Application.Current.GetService<WindowEx>().DispatcherQueue;

        dispatcherQueue.TryEnqueue(() =>
        {
            behavior.Remove(notification);
        });
    }
}
