#region Using directives
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.UI;
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
using OpcUa = UAManagedCore.OpcUa;
#endregion

public class UserEditorRolesPanelLogic : BaseNetLogic
{
    /// <summary>
    /// This method handles the start of the application, setting up variables, event handlers, and initializing the UI roles.
    /// </summary>
    /// <remarks>
    /// The method also checks if the editable flag is set and calls <see cref="SetCheckedValues()"/> if so.
    /// </remarks>
    public override void Start()
    {
        userVariable = Owner.GetVariable("User");
        editable = Owner.GetVariable("Editable");

        userVariable.VariableChange += UserVariable_VariableChange;
        editable.VariableChange += Editable_VariableChange;

        UpdateRolesAndUser();

        BuildUIRoles();
        if (editable.Value)
            SetCheckedValues();
    }

    /// <summary>
    /// This method handles the change in a variable and updates the roles and UI accordingly.
    /// If the new value is true, it sets the checked values.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The VariableChangeEventArgs instance containing the event data.</param>
    /// <remarks>
    /// The method updates roles and the UI based on the change in the variable value.
    /// If the new value is true, it calls SetCheckedValues() to update the checked states.
    /// </remarks>
    private void Editable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        UpdateRolesAndUser();
        BuildUIRoles();

        if (e.NewValue)
            SetCheckedValues();
    }

    /// <summary>
    /// Handles the variable change event, updating roles and user information based on the event.
    /// If the value is editable, it sets the checked values; otherwise, it builds the UI roles.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The VariableChangeEventArgs instance containing the event data.</param>
    private void UserVariable_VariableChange(object sender, VariableChangeEventArgs e)
    {
        UpdateRolesAndUser();
        if (editable.Value)
            SetCheckedValues();
        else
            BuildUIRoles();
    }

    /// <summary>
    /// This method updates the user and roles based on the provided variable.
    /// If the <see cref="userVariable.Value.Value"/> is not null, it retrieves the user
    /// using <see cref="InformationModel.Get"/> with the value from <see cref="userVariable.Value"/>.
    /// It also sets the <see cref="roles"/> to the alias of "Roles" using <see cref="LogicObject.GetAlias"/>.
    /// </summary>
    private void UpdateRolesAndUser()
    {
        if (userVariable.Value.Value != null)
            user = InformationModel.Get(userVariable.Value);

        roles = LogicObject.GetAlias("Roles");
    }

    /// <summary>
    /// This method builds the UI for roles, creating a container panel and adding UI elements
    /// based on whether the user is editable or has a specific role.
    /// </summary>
    /// <remarks>
    /// The method handles role management by creating a <see cref="ColumnLayout"/> container
    /// and adding UI objects based on the <see cref="editable.Value"/> and <see cref="UserHasRole"/>
    /// conditions. It ensures proper layout and positioning of UI elements.
    /// </remarks>
    private void BuildUIRoles()
    {
        if (roles == null)
            return;

        panel?.Delete();

        panel = InformationModel.MakeObject<ColumnLayout>("Container");
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.VerticalAlignment = VerticalAlignment.Top;
        panel.TopMargin = 0;

        foreach (var role in roles.Children)
        {
            Panel roleUiObject = null;

            if (editable.Value)
            {
                roleUiObject = InformationModel.MakeObject<Panel>(role.BrowseName, IRIS_IMM2_ROADSHOW2026_Landscape.ObjectTypes.GroupCheckbox);
            }
            else if (UserHasRole(role.NodeId))
            {
                roleUiObject = InformationModel.MakeObject<Panel>(role.BrowseName, IRIS_IMM2_ROADSHOW2026_Landscape.ObjectTypes.GroupLabel);
            }

            if (roleUiObject == null)
                continue;

            roleUiObject.HorizontalAlignment = HorizontalAlignment.Stretch;
            roleUiObject.VerticalAlignment = VerticalAlignment.Top;
            roleUiObject.TopMargin = 0;

            roleUiObject.GetVariable("Group").Value = role.NodeId;

            panel.Add(roleUiObject);
            panel.Height += roleUiObject.Height;
        }

        var scrollView = Owner.Get("ScrollView");
        scrollView?.Add(panel);
    }

    /// <summary>
    /// This method sets the checked status of various checkbox nodes in a panel based on whether the user has the corresponding role.
    /// </summary>
    /// <remarks>
    /// The method first checks if the <see cref="roles"/> and <see cref="panel"/> objects are not null. If either is null, it returns immediately.
    /// It then retrieves all checkbox nodes in the panel that reference components of type <see cref="OpcUa.ReferenceTypes.HasOrderedComponent"/>.
    /// For each such node, it retrieves the role associated with the node and sets the "Checked" property of the node to the result of <see cref="UserHasRole"/> for that role's <see cref="NodeId"/>.
    /// </remarks>
    private void SetCheckedValues()
    {
        if (roles == null)
            return;

        if (panel == null)
            return;

        var roleCheckBoxes = panel.Refs.GetObjects(OpcUa.ReferenceTypes.HasOrderedComponent, false);

        foreach (var roleCheckBoxNode in roleCheckBoxes)
        {
            var role = roles.Get(roleCheckBoxNode.BrowseName);
            roleCheckBoxNode.GetVariable("Checked").Value = UserHasRole(role.NodeId);
        }
    }

    /// <summary>
    /// Checks if the user has the specified role node ID.
    /// </summary>
    /// <param name="roleNodeId">The node ID of the role to check.</param>
    /// <returns>true if the user has the role; otherwise, false.</returns>
    private bool UserHasRole(NodeId roleNodeId)
    {
        if (user == null)
            return false;
        var userRoles = user.Refs.GetObjects(FTOptix.Core.ReferenceTypes.HasRole, false);
        foreach (var userRole in userRoles)
        {
            if (userRole.NodeId == roleNodeId)
                return true;
        }
        return false;
    }

    private IUAVariable userVariable;
    private IUAVariable editable;

    private IUANode roles;
    private IUANode user;
    private ColumnLayout panel;
}
