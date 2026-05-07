namespace Cocorra.DAL.AppMetaData
{
    public static class Router
    {
        public const string Root = "Api";
        public const string Version = "V1";
        public const string Rule = Root + "/" + Version + "/";

        public static class AuthenticationRouting
        {
            public const string Prefix = Rule + "Authentication";
            public const string Register = Prefix + "/Register";
            public const string Login = Prefix + "/Login";
            public const string SubmitMbti = Prefix + "/SubmitMbti";
            public const string ForgotPassword = Prefix + "/ForgotPassword";
            public const string UpdateFcmToken = Prefix + "/UpdateFcmToken";
            public const string ConfirmEmail = Prefix + "/ConfirmEmail"; // GET
            public const string ResendOtp = Prefix + "/ResendOtp"; // POST
            public const string ResetPassword = Prefix + "/ResetPassword"; // POST
            public const string ReRecordVoice = Prefix + "/ReRecordVoice"; // POST
            public const string UpdatePassword = Prefix + "/UpdatePassword"; // PUT
            public const string DeleteAccount = Prefix + "/DeleteAccount"; // DELETE
            public const string RefreshToken = Prefix + "/RefreshToken"; // POST
            public const string RevokeToken = Prefix + "/RevokeToken"; // POST
        }

        public static class AdminRouting
        {
            public const string Prefix = Rule + "Admin";

            public const string GetAll = Prefix + "/Users";               // GET
            public const string GetById = Prefix + "/User/{id}";          // GET
            public const string Update = Prefix + "/User/{id}";           // PUT
            public const string Delete = Prefix + "/User/{id}";           // DELETE           
            public const string ChangeStatus = Prefix + "/User/ChangeStatus/{id}"; // PUT

            public const string ResetPassword = Prefix + "/ResetPassword/{id}"; // POST
            public const string Stats = Prefix + "/Dashboard/Stats";      // GET
        }
        public static class RolesRouting
        {
            public const string Prefix = Rule + "Roles";
            public const string GetRoles = Prefix + "/List";               // GET
            public const string GetRoleById = Prefix + "/{id}";            // GET
            public const string Create = Prefix + "/Create";               // POST
            public const string Update = Prefix + "/Update";               // PUT
            public const string Delete = Prefix + "/Delete/{id}";          // DELETE
            public const string ManageUserRoles = Prefix + "/ManageUser";  // POST
            public const string GetUsersInRole = Prefix + "/Users/{roleName}"; // GET
        }
        public static class RoomRouting
        {
            public const string Prefix = Rule + "Room";
            public const string Create = Prefix + "/Create";                                // POST
            public const string Join = Prefix + "/{roomId:guid}/Join";                      // POST
            public const string Approve = Prefix + "/{roomId:guid}/Approve/{userId:guid}";  // POST
            public const string State = Prefix + "/{roomId:guid}/State";                    // GET
            public const string Feed = Prefix + "/Feed";                                    // GET
            public const string ToggleReminder = Prefix + "/{roomId:guid}/toggle-reminder"; // POST
            public const string Start = Prefix + "/{roomId:guid}/Start";                    // POST
            public const string End = Prefix + "/{roomId:guid}/End";                        // POST
            public const string AdminHistory = Prefix + "/admin/history";                    // GET
        }

        public static class ProfileRouting
        {
            public const string Prefix = Rule + "Profile";
            public const string UpdateAvatarPreset = Prefix + "/update-avatar-preset"; // PUT
        }

        public static class SupportRouting
        {
            public const string Prefix = Rule + "Support";
            public const string SubmitTicket = Prefix + "/Ticket"; // POST
            public const string SubmitReport = Prefix + "/Report"; // POST
            public const string AdminReports = Prefix + "/admin/reports"; // GET
            public const string AdminUpdateReportStatus = Prefix + "/admin/reports/{id:guid}/status"; // PUT
            public const string AdminTakeReportAction = Prefix + "/admin/reports/{id:guid}/action"; // POST
            public const string MyChat = Prefix + "/chat/my-chat"; // GET
        }

        public static class BlockRouting
        {
            public const string Prefix = Rule + "Users";
            public const string Block = Prefix + "/block/{targetId:guid}"; // POST
            public const string Unblock = Prefix + "/unblock/{targetId:guid}"; // DELETE
        }
    }
}