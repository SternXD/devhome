﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using DevHome.Services.WindowsPackageManager.Models;
using Microsoft.Management.Deployment;
using Windows.Foundation;

namespace DevHome.Services.WindowsPackageManager.Contracts;

internal interface IWinGetInstallOperation
{
    /// <summary>
    /// Installs a package from a URI.
    /// </summary>
    /// <param name="packageUri">Uri of the package to install.</param>
    /// <param name="activityId">Activity id for telemetry.</param>
    /// <returns>Result of the installation.</returns>
    public IAsyncOperationWithProgress<IWinGetInstallPackageResult, InstallProgress> InstallPackageAsync(WinGetPackageUri packageUri, Guid activityId);
}
