#region Using directives
using System;
using System.Linq;
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

public class CreateUserPanelLogic : BaseNetLogic
{
    /// <summary>
    /// Creates a new user with the specified username, password, and locale.
    /// The method sets the result to a default value (NodeId.Empty) and checks if the username is empty.
    /// If the username is empty, it shows a message and logs an error.
    /// Otherwise, it generates the user and assigns the result.
    /// </summary>
    /// <param name="username">The username for the new user.</param>
    /// <param name="password">The password for the new user.</param>
    /// <param name="locale">The locale setting for the new user.</param>
    /// <note>
    /// The method uses <see cref="NodeId.Empty"/> as the default value for <see cref="result"/>.
    /// </note>
    [ExportMethod]
    public void CreateUser(string username, string password, string locale, out NodeId result)
    {
        result = NodeId.Empty;

        if (string.IsNullOrEmpty(username))
        {
            ShowMessage(1);
            Log.Error("EditUserDetailPanelLogic", "Cannot create user with empty username");
            return;
        }

        result = GenerateUser(username, password, locale);
    }

    /// <summary>
    /// This method creates a new user with the specified username, password, and locale.
    /// It checks if the username already exists in the user list and handles various
    /// password change result codes.
    /// </summary>
    /// <param name="username">The username for the new user.</param>
    /// <param name="password">The password for the new user.</param>
    /// <param name="locale">The locale ID for the user (optional).</param>
    /// <returns>
    /// The NodeId of the newly created user, or NodeId.Empty if an error occurs.
    /// </returns>
    private NodeId GenerateUser(string username, string password, string locale)
    {
        var users = GetUsers();
        if (users == null)
        {
            ShowMessage(2);
            Log.Error("EditUserDetailPanelLogic", "Unable to get users");
            return NodeId.Empty;
        }

        foreach (var child in users.Children.OfType<FTOptix.Core.User>())
        {
            if (child.BrowseName.Equals(username, StringComparison.OrdinalIgnoreCase))
            {
                ShowMessage(10);
                Log.Error("EditUserDetailPanelLogic", "Username already exists");
                return NodeId.Empty;
            }
        }

        var user = InformationModel.MakeObject<FTOptix.Core.User>(username);
        users.Add(user);

        //Apply LocaleId
        if (!string.IsNullOrEmpty(locale))
            user.LocaleId = locale;

        //Apply groups
        ApplyGroups(user);

        //Apply roles
        ApplyRoles(user);

        //Apply password
        var result = Session.ChangePassword(username, password, string.Empty);

        switch (result.ResultCode)
        {
            case FTOptix.Core.ChangePasswordResultCode.Success:
                break;
            case FTOptix.Core.ChangePasswordResultCode.WrongOldPassword:
                //Not applicable
                break;
            case FTOptix.Core.ChangePasswordResultCode.PasswordAlreadyUsed:
                //Not applicable
                break;
            case FTOptix.Core.ChangePasswordResultCode.PasswordChangedTooRecently:
                //Not applicable
                break;
            case FTOptix.Core.ChangePasswordResultCode.PasswordTooShort:
                ShowMessage(6);
                users.Remove(user);
                return NodeId.Empty;
            case FTOptix.Core.ChangePasswordResultCode.UserNotFound:
                //Not applicable
                break;
            case FTOptix.Core.ChangePasswordResultCode.UnsupportedOperation:
                ShowMessage(8);
                users.Remove(user);
                return NodeId.Empty;

        }

        return user.NodeId;
    }

    /// <summary>
    /// This method retrieves and updates the groups and roles panel for a user,
    /// and updates references based on the user's group status.
    /// </summary>
    /// <param name="user">The user for whom the groups and roles panel is being accessed.</param>
    /// <remarks>
    /// The method accesses various UI components such as the "GroupsPanel",
    /// "Groups", and "ScrollView/Container" to update the state based on the user's
    /// group membership.
    /// </remarks>
    private void ApplyGroups(FTOptix.Core.User user)
    {
        var groupsPanel = Owner.Get("HorizontalLayout/GroupsAndRoles/GroupsPanel");
        var editable = groupsPanel.GetVariable("Editable");
        var groups = groupsPanel.GetAlias("Groups");
        var panel = groupsPanel.Get("ScrollView/Container");

        UpdateReferences(FTOptix.Core.ReferenceTypes.HasGroup, user, panel, groups, editable);
    }

    /// <summary>
    /// This method applies roles to a user by accessing UI components and updating references.
    /// </summary>
    /// <param name="user">The user to whom roles are applied.</param>
    /// <remarks>
    /// The method interacts with UI components to retrieve roles and editable status, then updates references to reflect the role changes.
    /// </remarks>
    private void ApplyRoles(FTOptix.Core.User user)
    {
        var groupsPanel = Owner.Get("HorizontalLayout/GroupsAndRoles/RolesPanel");
        var editable = groupsPanel.GetVariable("Editable");
        var roles = groupsPanel.GetAlias("Roles");
        var panel = groupsPanel.Get("ScrollView/Container");

        UpdateReferences(FTOptix.Core.ReferenceTypes.HasRole, user, panel, roles, editable);
    }

    /// <summary>
    /// This method updates references for a user based on the specified reference type.
    /// It checks if the user can be edited and updates the references accordingly.
    /// </summary>
    /// <param name="referenceType">The type of reference to update.</param>
    /// <param name="user">The user for whom the references are being updated.</param>
    /// <param name="panel">The panel containing the references.</param>
    /// <param name="rolesOrGroupsAlias">The alias for roles or groups.</param>
    /// <param name="editable">The editable status of the panel.</param>
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
    /// This method checks if the given user belongs to the specified group.
    /// </summary>
    /// <param name="referenceType">The type of reference to check.</param>
    /// <param name="user">The user object to check.</param>
    /// <param name="groupNodeId">The node ID of the group to check against.</param>
    /// <returns>
    /// True if the user belongs to the specified group, otherwise false.
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
    /// An <see cref="IUANode"/> object representing the resolved "Users" node.
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
    /// <param name="message">The message to be set as the error message.</param>
    /// <remarks>
    /// The method ensures that the delayed task is properly disposed of before creating a new one, to avoid memory leaks.
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
    /// If the task is cancelled, it immediately returns without executing.
    /// If an error message is available, it resets the error message value to 0.
    /// The method also disposes of the task if it is not null.
    /// </summary>
    /// <param name="task">The delayed task to be executed.</param>
    /// <remarks>
    /// </remarks>
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
