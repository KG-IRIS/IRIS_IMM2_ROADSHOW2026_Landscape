#region Using directives
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using UAManagedCore;
using FTOptix.OPCUAServer;
using FTOptix.Alarm;
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
using FTOptix.OPCUAClient;
#endregion

public class ChildrenCounter : BaseNetLogic
{
    /// <summary>
    /// This method initializes a new instance of the ChildrenCounter class.
    /// It sets up the logic object and initializes the references observer.
    /// </summary>
    public override void Start()
    {
        var nodePointerVariable = LogicObject.GetVariable("Node");
        if (nodePointerVariable == null)
        {
            Log.Error("ChildrenCounter", "Missing Node variable on ChildrenCounter");
            return;
        }

        var nodePointerValue = (NodeId)nodePointerVariable.Value;
        if (nodePointerValue == null || nodePointerValue == NodeId.Empty)
        {
            Log.Warning("ChildrenCounter", "Node variable not set");
            return;
        }

        var countVariable = LogicObject.GetVariable("Count");
        if (countVariable == null)
        {
            Log.Error("ChildrenCounter", "Missing variable Count on ChildrenCounter");
            return;
        }

        var resolvedResult = InformationModel.Get(nodePointerValue);
        countVariable.Value = resolvedResult.Children.Count;

        if (referencesEventRegistration != null)
        {
            referencesEventRegistration.Dispose();
            referencesEventRegistration = null;
        }

        referencesObserver = new ReferencesObserver(resolvedResult, countVariable);
        referencesObserver.Initialize();

        referencesEventRegistration = resolvedResult.RegisterEventObserver(
            referencesObserver, EventType.ForwardReferenceAdded | EventType.ForwardReferenceRemoved);
    }

    public override void Stop()
    {
        referencesEventRegistration?.Dispose();

        referencesEventRegistration = null;
        referencesObserver = null;
    }

    private class ReferencesObserver : IReferenceObserver
    {
        /// <summary>
        /// This method initializes a new instance of the ReferencesObserver class with the specified node to monitor and a count variable.
        /// </summary>
        /// <param name="nodeToMonitor">The node to monitor for changes.</param>
        /// <param name="countVariable">The variable to track the count of references.</param>
        /// <returns>
        /// A new instance of ReferencesObserver with the specified node and count variable initialized.
        /// </returns>
        public ReferencesObserver(IUANode nodeToMonitor, IUAVariable countVariable)
        {
            this.nodeToMonitor = nodeToMonitor;
            this.countVariable = countVariable;
        }

        /// <summary>
        /// This method sets the value of <see cref="countVariable"/> to the count of children in <see cref="nodeToMonitor"/>.
        /// </summary>
        /// <remarks>
        /// This method is used to initialize the count of monitored nodes.
        /// </remarks>
        public void Initialize()
        {
            countVariable.Value = nodeToMonitor.Children.Count;
        }

        /// <summary>
        /// This method increments the count variable when a reference is added between two nodes.
        /// </summary>
        /// <param name="sourceNode">The source node in the reference.</param>
        /// <param name="targetNode">The target node in the reference.</param>
        /// <param name="referenceTypeId">The type of reference being added.</param>
        /// <param name="senderId">The identifier of the sender node.</param>
        public void OnReferenceAdded(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
        {
            if (IsReferenceAllowed(referenceTypeId))
            {
                ++countVariable.Value;
            }
        }

        /// <summary>
        /// This method handles the removal of a reference, checking if the reference type is allowed
        /// and decrementing a counter if the counter is greater than zero.

        /// </summary>
        public void OnReferenceRemoved(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
        {
            if (IsReferenceAllowed(referenceTypeId) && countVariable.Value > 0)
            {
                --countVariable.Value;
            }
        }

        /// <summary>
        /// This method checks if a given reference type is allowed to be used.
        /// It returns true if the reference type is either 
        /// <see cref="UAManagedCore.OpcUa.ReferenceTypes.HasComponent"/> or 
        /// <see cref="UAManagedCore.OpcUa.ReferenceTypes.HasOrderedComponent"/>.
        /// </summary>
        /// <param name="referenceTypeId">The type identifier of the reference to check.</param>
        /// <returns>
        /// A boolean value indicating whether the reference type is allowed.
        /// </returns>
        public bool IsReferenceAllowed(NodeId referenceTypeId)
        {
            return referenceTypeId == UAManagedCore.OpcUa.ReferenceTypes.HasComponent ||
                   referenceTypeId == UAManagedCore.OpcUa.ReferenceTypes.HasOrderedComponent;
        }

        private readonly IUANode nodeToMonitor;
        private readonly IUAVariable countVariable;
    }

    private ReferencesObserver referencesObserver;
    private IEventRegistration referencesEventRegistration;
}
