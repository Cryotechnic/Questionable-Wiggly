﻿using System;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps;

namespace Questionable.Controller.CombatModules;

internal sealed class WrathComboModule : ICombatModule, IDisposable
{
    private const string CallbackPrefix = "Questionable$Wrath";

    private readonly ILogger<WrathComboModule> _logger;
    private readonly Configuration _configuration;
    private readonly ICallGateSubscriber<object> _test;
    private readonly ICallGateSubscriber<string, string, string, Guid?> _registerForLeaseWithCallback;
    private readonly ICallGateSubscriber<Guid, object> _releaseControl;
    private readonly ICallGateSubscriber<Guid,bool,object> _setAutoRotationState;
    private readonly ICallGateSubscriber<Guid,object> _setCurrentJobAutoRotationReady;
    private readonly ICallGateProvider<int, string, object> _callback;

    private Guid? _lease;

    public WrathComboModule(ILogger<WrathComboModule> logger, Configuration configuration,
        IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _configuration = configuration;
        _test = pluginInterface.GetIpcSubscriber<object>("WrathCombo.Test");
        _registerForLeaseWithCallback =
            pluginInterface.GetIpcSubscriber<string, string, string, Guid?>("WrathCombo.RegisterForLeaseWithCallback");
        _releaseControl = pluginInterface.GetIpcSubscriber<Guid, object>("WrathCombo.ReleaseControl");
        _setAutoRotationState = pluginInterface.GetIpcSubscriber<Guid, bool, object>("WrathCombo.SetAutoRotationState");
        _setCurrentJobAutoRotationReady =
            pluginInterface.GetIpcSubscriber<Guid, object>("WrathCombo.SetCurrentJobAutoRotationReady");

        _callback = pluginInterface.GetIpcProvider<int, string, object>($"{CallbackPrefix}.WrathComboCallback");
        _callback.RegisterAction(Callback);
    }

    public bool CanHandleFight(CombatController.CombatData combatData)
    {
        if (_configuration.General.CombatModule != Configuration.ECombatModule.WrathCombo)
            return false;

        try
        {
            _test.InvokeAction();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
    }

    public bool Start(CombatController.CombatData combatData)
    {
        try
        {
            _lease = _registerForLeaseWithCallback.InvokeFunc("Questionable", "Questionable", CallbackPrefix);
            if (_lease != null)
            {
                _logger.LogDebug("Wrath combo lease: {Lease}", _lease.Value);

                _setAutoRotationState.InvokeAction(_lease.Value, true);
                _setCurrentJobAutoRotationReady.InvokeAction(_lease.Value);
                return true;
            }
            else
            {
                _logger.LogError("Wrath combo did not return a lease");
                return false;
            }
        }
        catch (IpcError e)
        {
            _logger.LogError(e, "Unable to use wrath combo for combat");
            return false;
        }
    }

    public bool Stop()
    {
        try
        {
            if (_lease != null)
            {
                _releaseControl.InvokeAction(_lease.Value);
                _lease = null;
            }

            return true;
        }
        catch (IpcError e)
        {
            _logger.LogWarning(e, "Could not turn off wrath combo");
            return false;
        }
    }

    public void Update(IGameObject nextTarget)
    {
        if (_lease == null)
            throw new TaskException("Wrath Combo Lease is cancelled");
    }

    public bool CanAttack(IBattleNpc target) => true;

    private void Callback(int reason, string additionalInfo)
    {
        _logger.LogWarning("WrathCombo callback: {Reason} ({Info})", reason, additionalInfo);
        _lease = null;
    }

    public void Dispose()
    {
        Stop();
        _callback.UnregisterAction();
    }
}
