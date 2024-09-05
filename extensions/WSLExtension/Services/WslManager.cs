﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Serilog;
using Windows.System.Threading;
using WSLExtension.ClassExtensions;
using WSLExtension.Contracts;
using WSLExtension.DistributionDefinitions;
using WSLExtension.Helpers;
using WSLExtension.Models;
using static WSLExtension.Constants;

namespace WSLExtension.Services;

public class WslManager : IWslManager
{
    private readonly ILogger _log = Log.ForContext("SourceContext", nameof(WslManager));

    private readonly PackageHelper _packageHelper = new();

    private readonly TimeSpan _oneMinutePollingInterval = TimeSpan.FromMinutes(1);

    private readonly WslRegisteredDistributionFactory _wslRegisteredDistributionFactory;

    private readonly IWslServicesMediator _wslServicesMediator;

    private readonly IDistributionDefinitionHelper _definitionHelper;

    private readonly List<WslComputeSystem> _registeredWslDistributions = new();

    public event EventHandler<HashSet<string>>? DistributionStateSyncEventHandler;

    private Dictionary<string, DistributionDefinition>? _distributionDefinitionsMap;

    private ThreadPoolTimer? _timerForUpdatingDistributionStates;

    public WslManager(
        IWslServicesMediator wslServicesMediator,
        WslRegisteredDistributionFactory wslDistributionFactory,
        IDistributionDefinitionHelper distributionDefinitionHelper)
    {
        _wslRegisteredDistributionFactory = wslDistributionFactory;
        _wslServicesMediator = wslServicesMediator;
        _definitionHelper = distributionDefinitionHelper;
        StartDistributionStatePolling();
    }

    /// <inheritdoc cref="IWslManager.GetAllRegisteredDistributionsAsync"/>
    public async Task<List<WslComputeSystem>> GetAllRegisteredDistributionsAsync()
    {
        // The list of compute systems in Dev Home is being refreshed, so remove any old
        // subscriptions
        _registeredWslDistributions.ForEach(distribution => distribution.RemoveSubscriptions());
        _registeredWslDistributions.Clear();

        foreach (var distribution in await GetInformationOnAllRegisteredDistributionsAsync())
        {
            try
            {
                _registeredWslDistributions.Add(_wslRegisteredDistributionFactory(distribution.Value));
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Unable to add the distribution: {distribution.Key}");
            }
        }

        return _registeredWslDistributions;
    }

    /// <inheritdoc cref="IWslManager.GetAllDistributionsAvailableToInstallAsync"/>
    public async Task<List<DistributionDefinition>> GetAllDistributionsAvailableToInstallAsync()
    {
        var registeredDistributionsMap = await GetInformationOnAllRegisteredDistributionsAsync();
        var distributionsToListOnCreationPage = new List<DistributionDefinition>();
        _distributionDefinitionsMap ??= await _definitionHelper.GetDistributionDefinitionsAsync();
        foreach (var distributionDefinition in _distributionDefinitionsMap.Values)
        {
            // filter out distribution definitions already registered on machine.
            if (registeredDistributionsMap.TryGetValue(distributionDefinition.Name, out var _))
            {
                continue;
            }

            distributionsToListOnCreationPage.Add(distributionDefinition);
        }

        // Sort the list by distribution name in ascending order before sending it.
        distributionsToListOnCreationPage.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return distributionsToListOnCreationPage;
    }

    /// <inheritdoc cref="IWslManager.GetInformationOnRegisteredDistributionAsync"/>
    public async Task<WslRegisteredDistribution?> GetInformationOnRegisteredDistributionAsync(string distributionName)
    {
        foreach (var registeredDistribution in (await GetInformationOnAllRegisteredDistributionsAsync()).Values)
        {
            if (distributionName.Equals(registeredDistribution.Name, StringComparison.Ordinal))
            {
                return registeredDistribution;
            }
        }

        return null;
    }

    /// <inheritdoc cref="IWslManager.IsDistributionRunning"/>
    public bool IsDistributionRunning(string distributionName)
    {
        return _wslServicesMediator.IsDistributionRunning(distributionName);
    }

    /// <inheritdoc cref="IWslManager.UnregisterDistribution"/>
    public void UnregisterDistribution(string distributionName)
    {
        _wslServicesMediator.UnregisterDistribution(distributionName);
    }

    /// <inheritdoc cref="IWslManager.LaunchDistribution"/>
    public void LaunchDistribution(string distributionName)
    {
        _wslServicesMediator.LaunchDistribution(distributionName);
    }

    /// <inheritdoc cref="IWslManager.InstallDistribution"/>
    public void InstallDistribution(string distributionName)
    {
        _wslServicesMediator.InstallDistribution(distributionName);
    }

    /// <inheritdoc cref="IWslManager.TerminateDistribution"/>
    public void TerminateDistribution(string distributionName)
    {
        _wslServicesMediator.TerminateDistribution(distributionName);
    }

    /// <summary>
    /// Retrieves information about all registered distributions on the machine and fills in any missing data
    /// that is needed for them to be shown in Dev Home's UI. E.g logo images.
    /// </summary>
    private async Task<Dictionary<string, WslRegisteredDistribution>> GetInformationOnAllRegisteredDistributionsAsync()
    {
        _distributionDefinitionsMap ??= await _definitionHelper.GetDistributionDefinitionsAsync();
        var distributions = new Dictionary<string, WslRegisteredDistribution>();
        foreach (var distribution in _wslServicesMediator.GetAllRegisteredDistributions())
        {
            // If this is a distribution we know about in DistributionDefinition.yaml add its friendly name and logo.
            if (_distributionDefinitionsMap.TryGetValue(distribution.Name, out var knownDistributionInfo))
            {
                distribution.FriendlyName = knownDistributionInfo.FriendlyName;
                distribution.Base64StringLogo = knownDistributionInfo.Base64StringLogo;
                distribution.AssociatedTerminalProfileGuid = knownDistributionInfo.WindowsTerminalProfileGuid;
            }

            distributions.Add(distribution.Name, distribution);
        }

        return distributions;
    }

    /// <summary>
    /// Raises an event once every minute so that the wsl compute systems state can be updated. Unfortunately there
    /// are no WSL APIs to achieve this. Once an API is created that fires an event for state changes this can be
    /// updated/removed.
    /// </summary>
    private void StartDistributionStatePolling()
    {
        _timerForUpdatingDistributionStates = ThreadPoolTimer.CreatePeriodicTimer(
            (ThreadPoolTimer timer) =>
            {
                try
                {
                    DistributionStateSyncEventHandler?.Invoke(this, _wslServicesMediator.GetAllNamesOfRunningDistributions());
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Unable to raise distribution sync event due to an error");
                }
            },
            _oneMinutePollingInterval);
    }
}
