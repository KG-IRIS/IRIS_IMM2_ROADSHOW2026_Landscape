#region Using directives
using System;
using System.Linq;
using UAManagedCore;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.WebUI;
using FTOptix.RecipeX;
using FTOptix.Store;
using FTOptix.Report;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.SQLiteStore;
using FTOptix.ODBCStore;
using FTOptix.InfluxDBStore;
using FTOptix.InfluxDBStoreRemote;
using FTOptix.InfluxDBStoreLocal;
using FTOptix.EventLogger;
using FTOptix.DataLogger;
using FTOptix.MQTTClient;
using FTOptix.OPCUAServer;
using FTOptix.OPCUAClient;
#endregion

public class AlarmBannerLogic : BaseNetLogic
{
    private const int MIN_ROTATION_TIME = 500;
    private enum MoveDirection { Forward, Backward };

    public override void Start()
    {
        affinityId = LogicObject.Context.AssignAffinityId();

        currentDisplayedAlarm = LogicObject.GetVariable("CurrentDisplayedAlarm");
        currentDisplayedAlarm.Value = NodeId.Empty;

        currentDisplayedAlarmIndex = LogicObject.GetVariable("CurrentDisplayedAlarmIndex");
        currentDisplayedAlarmIndex.Value = 0;

        RegisterObserverOnLocalizedAlarmsContainer(LogicObject.Context);
        RegisterObserverOnSessionActualLanguageChange(LogicObject.Context);
        RegisterObserverOnLocalizedAlarmsObject(LogicObject.Context);

        retainedAlarmsCount = LogicObject.GetVariable("AlarmCount");
        retainedAlarmsCount.Value = localizedAlarmsContainer?.Children.Count ?? 0;

        rotationTime = LogicObject.GetVariable("RotationTime");
        rotationTime.VariableChange += (_, __) => RestartRotationAndMoveAlarmLock(MoveDirection.Forward);

        RestartRotationAndMoveAlarmLock(MoveDirection.Forward);
    }

    public override void Stop()
    {
        alarmEventRegistration?.Dispose();
        alarmEventRegistration2?.Dispose();
        sessionActualLanguageRegistration?.Dispose();
        rotationTask?.Dispose();

        alarmEventRegistration = null;
        alarmEventRegistration2 = null;
        sessionActualLanguageRegistration = null;
        rotationTask = null;
    }

    #region Exported user methods
    /// <summary>
    /// This method restarts the rotation and moves the alarm lock in the forward direction.
    /// </summary>
    /// <remarks>
    /// The method assumes that <see cref="RestartRotationAndMoveAlarmLock"/> is a method that
    /// handles the actual rotation and lock movement logic.
    /// </remarks>
    [ExportMethod]
    public void NextAlarm()
    {
        RestartRotationAndMoveAlarmLock(MoveDirection.Forward);
    }

    /// <summary>
    /// This method restarts the rotation and moves the alarm lock in the backward direction.
    /// </summary>
    /// <remarks>
    /// The method calls <see cref="RestartRotationAndMoveAlarmLock(MoveDirection.Backward)"/> to perform the action.
    /// </remarks>
    [ExportMethod]
    public void PreviousAlarm()
    {
        RestartRotationAndMoveAlarmLock(MoveDirection.Backward);
    }
    #endregion

    #region Alarms specific events
    /// <summary>
    /// This method handles the addition of an alarm, updating the retained alarm count and deciding whether to restart the rotation and move the alarm.
    /// </summary>
    /// <param name="sourceNode">The source node of the alarm addition.</param>
    /// <param name="targetNode">The target node where the alarm is being added.</param>
    /// <param name="referenceTypeId">The type identifier of the reference being added.</param>
    /// <param name="senderId">The identifier of the sender of the alarm addition.</param>
    private void OnAlarmAdded(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
    {
        lock (_timerLock)
        {
            retainedAlarmsCount.Value = localizedAlarmsContainer?.Children.Count ?? 0;
            if (currentDisplayedAlarm.Value == UAValue.Null)
            {
                // Set any initial value to prevent multiple calls in the following scenario:
                // 1. First alarm is added and because currentDisplayedAlarm.Value is not set yet the RestartRotationTaskAndMoveAlarm is executed.
                // 2. Then before the periodic task is started, so currentDisplayedAlarm.Value is still not set,
                // another alarm is added and the RestartRotationTaskAndMoveAlarm is executed again.
                // This situation will not lead to an error but banner will start showing e.g. second alarm instead of the first one.
                currentDisplayedAlarm.Value = localizedAlarmsContainer?.Children.ElementAtOrDefault<IUANode>(0)?.NodeId ?? NodeId.Empty;
                RestartRotationAndMoveAlarm(MoveDirection.Forward);
            }
        }
    }

    /// <summary>
    /// This method handles the removal of an alarm, updating the retained alarm count and deciding whether to restart the rotation and move the alarm.
    /// </summary>
    /// <param name="sourceNode">The source node of the alarm removal.</param>
    /// <param name="targetNode">The target node where the alarm is being removed.</param>
    /// <param name="referenceTypeId">The type identifier of the reference being removed.</param>
    /// <param name="senderId">The identifier of the sender of the alarm removal.</param>
    /// <remarks>
    /// The method uses a lock to ensure thread safety when updating the retained alarm count and deciding on the alarm movement.
    /// </remarks>
    private void OnAlarmRemoved(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
    {
        lock (_timerLock)
        {
            retainedAlarmsCount.Value = localizedAlarmsContainer?.Children.Count ?? 0;
            if ((int)retainedAlarmsCount.Value == 0 || targetNode.NodeId == (NodeId)currentDisplayedAlarm.Value)
                RestartRotationAndMoveAlarm(MoveDirection.Forward);
        }
    }
    #endregion

    #region AlarmBanner iterates alarms list
    /// <summary>
    /// This method moves the alarm in the specified direction (forward or backward) and updates the current displayed alarm.
    /// </summary>
    /// <param name="moveDirection">The direction to move the alarm (forward or backward).</param>
    /// <remarks>
    /// The method uses a lock to ensure thread safety when updating the alarm index and current displayed alarm.
    /// </remarks>
    private void MoveAlarm(MoveDirection moveDirection)
    {
        if (moveDirection == MoveDirection.Forward)
            IncrementAlarmIndex();
        else
            DecrementAlarmIndex();
        GoToCurrentAlarm();
    }

    /// <summary>
    /// This method increments the alarm index and ensures it wraps around if it exceeds the number of alarms.
    /// </summary>
    /// <remarks>
    /// The method checks the count of alarms in the localized alarms container and resets the index if necessary.
    /// </remarks>
    private void IncrementAlarmIndex()
    {
        alarmIndex++;
        int alarmsCount = localizedAlarmsContainer?.Children.Count ?? 0;
        if (alarmsCount == 0)
            alarmIndex = -1;
        else if (alarmIndex >= alarmsCount)
            alarmIndex = 0; // ensure endless loop over alarms
    }

    /// <summary>
    /// This method decrements the alarm index and ensures it wraps around if it goes below zero.
    /// </summary>
    private void DecrementAlarmIndex()
    {
        alarmIndex--;
        if (alarmIndex < 0)
            alarmIndex = (localizedAlarmsContainer?.Children.Count ?? 0) - 1; // ensure endless loop over alarms
        if (alarmIndex < 0)
            alarmIndex = 0;
    }

    /// <summary>
    /// This method retrieves the current alarm from the localized alarms container and updates the current displayed alarm variable.
    /// </summary>
    private void GoToCurrentAlarm()
    {
        IUANode alarm = localizedAlarmsContainer?.Children.ElementAtOrDefault<IUANode>(alarmIndex);
        if (alarmIndex > 0 && alarm == null)
        {
            alarmIndex = 0; // reset in case moving to current alarm is no longer possible
            alarm = localizedAlarmsContainer?.Children.ElementAtOrDefault<IUANode>(alarmIndex);
        }
        if (alarm != null)
            currentDisplayedAlarm.Value = alarm.NodeId;
        else
            currentDisplayedAlarm.Value = NodeId.Empty;
        currentDisplayedAlarmIndex.Value = alarmIndex;
    }
    #endregion

    #region Alarm observers
    /// <summary>
    /// Registers an observer for the localized alarms object.
    /// </summary>
    /// <param name="context">The context in which the operation is performed.</param>
    private void RegisterObserverOnLocalizedAlarmsObject(IContext context)
    {
        var retainedAlarms = context.GetNode(FTOptix.Alarm.Objects.RetainedAlarms);

        retainedAlarmsObjectObserver = new RetainedAlarmsObjectObserver((ctx) => RegisterObserverOnLocalizedAlarmsContainer(ctx));

        alarmEventRegistration2 = retainedAlarms.RegisterEventObserver(
            retainedAlarmsObjectObserver, EventType.ForwardReferenceAdded, affinityId);
    }

    /// <summary>
    /// Registers an observer for alarms in the localized alarms container.
    /// </summary>
    /// <param name="context">The context in which the operation is performed.</param>
    /// <returns>
    /// A reference to the alarm event registration object.
    /// </returns>
    /// <remarks>
    /// The method checks if the localized alarms node is available and registers an observer
    /// for addition and removal of alarms. It also cleans up any previous registration
    /// and sets up the observer with the appropriate event types and affinity ID.
    /// </remarks>
    private void RegisterObserverOnLocalizedAlarmsContainer(IContext context)
    {
        var retainedAlarms = context.GetNode(FTOptix.Alarm.Objects.RetainedAlarms);
        var localizedAlarmsVariable = retainedAlarms.GetVariable("LocalizedAlarms");
        var localizedAlarmsNodeId = (NodeId)localizedAlarmsVariable.Value;
        if (localizedAlarmsNodeId != null && !localizedAlarmsNodeId.IsEmpty)
            localizedAlarmsContainer = LogicObject.Context.GetNode(localizedAlarmsNodeId);

        if (alarmEventRegistration != null)
        {
            alarmEventRegistration.Dispose();
            alarmEventRegistration = null;
        }

        var alarmsAddRemoveObserver = new AlarmsObserver(this);
        alarmEventRegistration = localizedAlarmsContainer?.RegisterEventObserver(
            alarmsAddRemoveObserver,
            EventType.ForwardReferenceAdded |
            EventType.ForwardReferenceRemoved,
            affinityId);
    }

    /// <summary>
    /// Registers an observer for changes in the "ActualLanguage" property of the current session.
    /// When the "ActualLanguage" value changes, it triggers the <see cref="RegisterObserverOnLocalizedAlarmsContainer"/> method.
    /// </summary>
    /// <param name="context">The context object containing session information.</param>
    /// <returns>
    /// No return value (void).
    /// </returns>
    private void RegisterObserverOnSessionActualLanguageChange(IContext context)
    {
        var currentSessionActualLanguage = context.Sessions.CurrentSessionInfo.SessionObject.Children["ActualLanguage"];

        sessionActualLanguageChangeObserver = new CallbackVariableChangeObserver(
            (IUAVariable variable, UAValue newValue, UAValue oldValue, ElementAccess elementAccess, ulong senderId) =>
            {
                RegisterObserverOnLocalizedAlarmsContainer(context);
            });

        sessionActualLanguageRegistration = currentSessionActualLanguage.RegisterEventObserver(
            sessionActualLanguageChangeObserver, EventType.VariableValueChanged, affinityId);
    }

    private class RetainedAlarmsObjectObserver : IReferenceObserver
    {
        /// <summary>
        /// This method initializes a retained alarms object observer with a callback action.
        /// </summary>
        /// <param name="action">The action to be executed when alarms are triggered.</param>
        /// <returns>
        /// A RetainedAlarmsObjectObserver instance with the provided callback.
        /// </returns>
        public RetainedAlarmsObjectObserver(Action<IContext> action)
        {
            registrationCallback = action;
        }

        /// <summary>
        /// This method handles the addition of a reference to a node, checking if the target node's browse name matches the current session's locale.
        /// If it does, it triggers the registration callback for the node.
        /// </summary>
        /// <param name="sourceNode">The source node in the reference.</param>
        /// <param name="targetNode">The target node being referenced.</param>
        /// <param name="referenceTypeId">The type of reference being added.</param>
        /// <param name="senderId">The sender identifier for the reference.</param>
        /// <remarks>
        /// The method assumes that <see cref="targetNode.Context.Sessions.CurrentSessionHandler.ActualLocaleId"/> is available and returns without further action if the locale does not match.
        /// </remarks>
        public void OnReferenceAdded(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
        {
            string localeId = targetNode.Context.Sessions.CurrentSessionHandler.ActualLocaleId;
            if (String.IsNullOrEmpty(localeId))
                localeId = "en-US";

            if (targetNode.BrowseName == localeId)
                registrationCallback(targetNode.Context);
        }

        /// <summary>
        /// This method is called when a reference is removed. It takes three parameters: the source node, the target node, the type of reference, and the sender ID. The method does not perform any action as it is a placeholder for future implementation.
        /// </summary>
        /// <param name="sourceNode">The source node in the reference relationship.</param>
        /// <param name="targetNode">The target node in the reference relationship.</param>
        /// <param name="referenceTypeId">The type of reference being removed.</param>
        /// <param name="senderId">The ID of the sender of the reference.</param>
        /// <returns>
        /// A value indicating the result of the operation. Currently, this method does not return a value.
        /// </returns>
        public void OnReferenceRemoved(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
        {
        }

        private Action<IContext> registrationCallback;
    }

    /// <summary>
    /// This method restarts the rotation and moves the alarm lock, ensuring thread safety with a lock statement.
    /// </summary>
    /// <param name="direction">The direction to restart the rotation and move the alarm.</param>
    private void RestartRotationAndMoveAlarmLock(MoveDirection direction)
    {
        lock (_timerLock)
        {
            RestartRotationAndMoveAlarm(direction);
        }
    }

    /// <summary>
    /// This method cancels the current rotation task and starts a new periodic task to rotate and move the alarm in the specified direction.
    /// </summary>
    /// <param name="direction">The direction to rotate and move the alarm.</param>
    private void RestartRotationAndMoveAlarm(MoveDirection direction)
    {
        rotationTask?.Cancel();
        MoveDirection currentDirection = direction;
        void MoveAlarmInternalPeriodic()
        {
            lock (_alarmLock)
            {
                MoveAlarm(currentDirection);
                currentDirection = MoveDirection.Forward;
            }
        }
        rotationTask = new PeriodicTask(MoveAlarmInternalPeriodic, RotationTimeVerification(rotationTime.Value), LogicObject);
        rotationTask.Start();
    }

    /// <summary>
    /// This method verifies the rotation time and returns the valid rotation time.
    /// If the input rotation time is less than the minimum allowed rotation time, it
    /// sets the rotation time to the minimum allowed value and logs a warning.
    /// Otherwise, it returns the input rotation time.
    /// </summary>
    /// <param name="rotationTimeMilliseconds">The rotation time in milliseconds to verify.</param>
    /// <returns>
    /// The verified rotation time, either the input value or the minimum allowed value
    /// if the input is too low.
    /// </returns>
    /// <remarks>
    /// The method also updates the previous rotation time value for future checks.
    /// </remarks>
    private int RotationTimeVerification(int rotationTimeMilliseconds)
    {
        int returnRotationTime;
        if (rotationTimeMilliseconds < MIN_ROTATION_TIME)
        {
            if (rotationTimePreviousValue != rotationTimeMilliseconds)
                Log.Warning("AlarmBanner", $"Rotation interval is too low: {rotationTimeMilliseconds}[ms]. Setting minimal rotation interval {MIN_ROTATION_TIME}[ms].");
            returnRotationTime = MIN_ROTATION_TIME;
        }
        else
        {
            returnRotationTime = rotationTimeMilliseconds;
        }
        rotationTimePreviousValue = rotationTimeMilliseconds;
        return returnRotationTime;
    }

    private class AlarmsObserver : IReferenceObserver
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AlarmsObserver"/> class with the specified <see cref="AlarmBannerLogic"/>.
        /// </summary>
        /// <param name="alarmsObject">The alarm logic object to be assigned to the observer.</param>
        /// <returns>
        /// A new instance of the <see cref="AlarmsObserver"/> class with the specified <see cref="AlarmBannerLogic"/>.
        /// </returns>
        public AlarmsObserver(AlarmBannerLogic _alarmsObject)
        {
            alarmsObject = _alarmsObject;
        }

        /// <summary>
        /// This method triggers the alarm system when a reference is added between two nodes.
        /// It passes the necessary parameters to the alarm handler to process the alarm.
        /// </summary>
        /// <param name="sourceNode">The source node in the reference relationship.</param>
        /// <param name="targetNode">The target node in the reference relationship.</param>
        /// <param name="referenceTypeId">The type of reference being added.</param>
        /// <param name="senderId">The identifier of the sender node.</param>
        public void OnReferenceAdded(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
        {
            alarmsObject.OnAlarmAdded(sourceNode, targetNode, referenceTypeId, senderId);
        }
        /// <summary>
        /// This method triggers the removal of an alarm when a reference is removed.
        /// It calls the <see cref="alarmsObject.OnAlarmRemoved"/> method to handle the alarm removal.
        /// </summary>
        /// <param name="sourceNode">The source node being removed.</param>
        /// <param name="targetNode">The target node being removed.</param>
        /// <param name="referenceTypeId">The type of reference being removed.</param>
        /// <param name="senderId">The sender identifier of the reference removal.</param>
        /// <remarks>
        /// The method is designed to handle the removal of a reference and associated alarms,
        /// ensuring that any relevant alarms are properly updated or removed.
        /// </remarks>
        public void OnReferenceRemoved(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
        {
            alarmsObject.OnAlarmRemoved(sourceNode, targetNode, referenceTypeId, senderId);
        }
        private AlarmBannerLogic alarmsObject;
    }
    #endregion

    private uint affinityId;
    private IEventRegistration alarmEventRegistration;
    private IEventRegistration alarmEventRegistration2;
    private IEventRegistration sessionActualLanguageRegistration;
    private RetainedAlarmsObjectObserver retainedAlarmsObjectObserver;
    private IEventObserver sessionActualLanguageChangeObserver;
    private IUANode localizedAlarmsContainer = null;
    private PeriodicTask rotationTask;
    private int alarmIndex = -1;
    private IUAVariable retainedAlarmsCount;
    private IUAVariable currentDisplayedAlarm;
    private IUAVariable currentDisplayedAlarmIndex;
    private IUAVariable rotationTime;
    private int rotationTimePreviousValue = -1;
    private readonly object _alarmLock = new object();
    private readonly object _timerLock = new object();
}
