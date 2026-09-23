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

public class EditUserDetailPanelLogic : BaseNetLogic
{
    /// <summary>
    /// This method saves a user with the specified username, password, and locale.
    /// It sets the result to <see cref="NodeId.Empty" /> by default.
    /// If the username is empty or null, it shows a message and logs an error.
    /// </summary>
    /// <param name="username">The username to save.</param>
    /// <param name="password">The password to save.</param>
    /// <param name="locale">The locale to use for the user.</param>
    /// <param name="result">The result node ID, assigned by the method.</param>
    [ExportMethod]
    public void SaveUser(string username, string password, string locale, out NodeId result)
    {
        result = NodeId.Empty;

        if (string.IsNullOrEmpty(username))
        {
            ShowMessage(1);
            Log.Error("EditUserDetailPanelLogic", "Cannot create user with empty username");
            return;
        }

        result = ApplyUser(username, password, locale);
    }

    private NodeId ApplyUser(string username, string password, string locale)
    {
        var users = GetUsers();
        if (users == null)
        {
            ShowMessage(2);
            Log.Error("EditUserDetailPanelLogic", "Unable to get users");
            return NodeId.Empty;
        }

        var user = users.Get<FTOptix.Core.User>(username);
        if (user == null)
        {
            ShowMessage(3);
            Log.Error("EditUserDetailPanelLogic", "User not found");
            return NodeId.Empty;
        }

        //Apply LocaleId
        if (!string.IsNullOrEmpty(locale))
            user.LocaleId = locale;

        //Apply groups
        ApplyGroups(user);

        //Apply roles
        ApplyRoles(user);

        //Apply password
        if (!string.IsNullOrEmpty(password))
        {
            var result = Session.ChangePasswordInternal(username, password);

            switch (result.ResultCode)
            {
                case FTOptix.Core.ChangePasswordResultCode.Success:
                    var editPasswordTextboxPtr = LogicObject.GetVariable("PasswordTextbox");
                    if (editPasswordTextboxPtr == null)
                    {
                        Log.Error("EditUserDetailPanelLogic", "PasswordTextbox variable not found");
                        break;
                    }

                    var nodeId = (NodeId)editPasswordTextboxPtr.Value;
                    if (nodeId == null)
                    {
                        Log.Error("EditUserDetailPanelLogic", "PasswordTextbox not set");
                        break;
                    }

                    var editPasswordTextbox = (TextBox)InformationModel.Get(nodeId);
                    if (editPasswordTextbox == null)
                    {
                        Log.Error("EditUserDetailPanelLogic", "EditPasswordTextbox not found");
                        break;
                    }

                    editPasswordTextbox.Text = string.Empty;
                    break;
                case FTOptix.Core.ChangePasswordResultCode.WrongOldPassword:
                    //Not applicable
                    break;
                case FTOptix.Core.ChangePasswordResultCode.PasswordAlreadyUsed:
                    ShowMessage(4);
                    return NodeId.Empty;
                case FTOptix.Core.ChangePasswordResultCode.PasswordChangedTooRecently:
                    ShowMessage(5);
                    return NodeId.Empty;
                case FTOptix.Core.ChangePasswordResultCode.PasswordTooShort:
                    ShowMessage(6);
                    return NodeId.Empty;
                case FTOptix.Core.ChangePasswordResultCode.UserNotFound:
                    ShowMessage(7);
                    return NodeId.Empty;
                case FTOptix.Core.ChangePasswordResultCode.UnsupportedOperation:
                    ShowMessage(8);
                    return NodeId.Empty;

            }
        }

        ShowMessage(9);
        return user.NodeId;
    }

    /// <summary>
    /// This method retrieves and updates the groups and roles panel for a user,
    /// and updates references based on the user's group status.
    /// </summary>
    /// <param name="user">The user for whom the groups and roles panel is being accessed.</param>
    /// <remarks>
    /// The method accesses various UI components such as the GroupsPanel, editable state,
    /// and a ScrollView container to update references related to the user's group membership.
    /// </remarks>
    private void ApplyGroups(FTOptix.Core.User user)
    {
        var groupsPanel = Owner.Get("HorizontalLayout1/GroupsAndRoles/GroupsPanel");
        var editable = groupsPanel.GetVariable("Editable");
        var groups = groupsPanel.GetAlias("Groups");
        var panel = groupsPanel.Get("ScrollView/Container");

        UpdateReferences(FTOptix.Core.ReferenceTypes.HasGroup, user, panel, groups, editable);
    }

    /// <summary>
    /// This method applies roles to a user by accessing UI components and updating references.
    /// </summary>
    /// <param name="user">The user to apply the roles to.</param>
    /// <remarks>
    /// The method interacts with UI components to retrieve roles and editable status,
    /// then updates references to reflect the role assignments.
    /// </remarks>
    private void ApplyRoles(FTOptix.Core.User user)
    {
        var groupsPanel = Owner.Get("HorizontalLayout1/GroupsAndRoles/RolesPanel");
        var editable = groupsPanel.GetVariable("Editable");
        var roles = groupsPanel.GetAlias("Roles");
        var panel = groupsPanel.Get("ScrollView/Container");

        UpdateReferences(FTOptix.Core.ReferenceTypes.HasRole, user, panel, roles, editable);
    }

    /// <summary>
    /// This method updates references for a user based on the specified reference type,
    /// user, panel, roles or groups alias, and editable state.
    /// </summary>
    /// <param name="referenceType">The type of reference to update.</param>
    /// <param name="user">The user to update references for.</param>
    /// <param name="panel">The panel containing the references.</param>
    /// <param name="rolesOrGroupsAlias">The alias for roles or groups.</param>
    /// <param name="editable">The editable state of the panel.</param>
    private static void UpdateReferences(NodeId referenceType, IUANode user, IUANode panel, IUANode rolesOrGroupsAlias, IUAVariable editable)
    {
        if (!editable.Value)
        {
            Log.Debug("EditUserDetailPanelLogic", "User cannot be edited");
            return;
        }

        if (user == null || rolesOrGroupsAlias == null || panel == null)
        {
            Log.Error("EditUserDetailPanelLogic", "User, roles or groups alias or panel not found");
            return;
        }

        var userNode = InformationModel.Get(user.NodeId);
        if (userNode == null)
        {
            Log.Error("EditUserDetailPanelLogic", "User node not found");
            return;
        }

        var referenceCheckBoxes = panel.Refs.GetObjects(OpcUa.ReferenceTypes.HasOrderedComponent, false);

        foreach (var referenceCheckBoxNode in referenceCheckBoxes)
        {
            var role = rolesOrGroupsAlias.Get(referenceCheckBoxNode.BrowseName);
            if (role == null)
            {
                Log.Error("EditUserDetailPanelLogic", "Role or group not found");
                return;
            }

            bool userHasReference = UserHasReference(referenceType, user, role.NodeId);

            if (referenceCheckBoxNode.GetVariable("Checked").Value && !userHasReference)
            {
                Log.Debug("EditUserDetailPanelLogic", $"Adding reference {referenceType} to user {user.NodeId} for role {role.NodeId}");
                userNode.Refs.AddReference(referenceType, role);
            }
            else if (!referenceCheckBoxNode.GetVariable("Checked").Value && userHasReference)
            {
                Log.Debug("EditUserDetailPanelLogic", $"Removing reference {referenceType} from user {user.NodeId} for role {role.NodeId}");
                userNode.Refs.RemoveReference(referenceType, role.NodeId, false);
            }
        }
    }

    /// <summary>
    /// This method checks if the given user is part of the specified group.
    /// </summary>
    /// <param name="referenceType">The type of reference to check.</param>
    /// <param name="user">The user to check membership for.</param>
    /// <param name="groupNodeId">The node ID of the group to check against.</param>
    /// <returns>
    /// A boolean value indicating whether the user is part of the specified group.
    /// </returns>
    private static bool UserHasReference(NodeId referenceType, IUANode user, NodeId groupNodeId)
    {
        if (user == null)
            return false;
        var userGroups = user.Refs.GetObjects(referenceType, false);
        foreach (var userGroup in userGroups)
        {
            if (userGroup.NodeId == groupNodeId)
                return true;
        }
        return false;
    }

    /// <summary>
    /// This method resolves the path to the "Users" node and returns it if found.
    /// </summary>
    /// <returns>
    /// An IUANode object representing the resolved "Users" node, or null if the path is not found.
    /// </returns>
    private IUANode GetUsers()
    {
        var pathResolverResult = LogicObject.Context.ResolvePath(LogicObject, "{Users}");
        if (pathResolverResult == null)
            return null;
        if (pathResolverResult.ResolvedNode == null)
            return null;

        return pathResolverResult.ResolvedNode;
    }

    /// <summary>
    /// This method sets the specified message as the error message and schedules a delayed task to execute the delayed action after 5 seconds.
    /// </summary>
    /// <param name="message">The message to set as the error message.</param>
    /// <remarks>
    /// The method first checks if there is an existing error message variable. If it exists, it updates its value to the new message. Then it disposes of any existing delayed task and creates a new one with the specified delay and action.
    /// </remarks>
    private void ShowMessage(int message)
    {
        var errorMessageVariable = LogicObject.GetVariable("ErrorMessage");
        if (errorMessageVariable != null)
            errorMessageVariable.Value = message;

        delayedTask?.Dispose();
        delayedTask = new DelayedTask(DelayedAction, 5000, LogicObject);
        delayedTask.Start();
    }

    /// <summary>
    /// This method handles a delayed action based on a provided task.
    /// If the task is canceled, it returns immediately without executing the action.
    /// If an error message variable is present, it resets its value to 0.
    /// The method also disposes of the task if it is not null.
    /// </summary>
    /// <param name="task">The delayed task to be executed.</param>
    private void DelayedAction(DelayedTask task)
    {
        if (task.IsCancellationRequested)
            return;

        var errorMessageVariable = LogicObject.GetVariable("ErrorMessage");
        if (errorMessageVariable != null)
        {
            errorMessageVariable.Value = 0;
        }
        delayedTask?.Dispose();
    }

    private DelayedTask delayedTask;
}
