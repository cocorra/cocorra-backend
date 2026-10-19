namespace Cocorra.DAL.DTOS.Role
{
    public class UpdateRoleDisplayNameDto
    {
        /// <summary>The new label. Empty or whitespace resets the role to showing its Name.</summary>
        public string? DisplayName { get; set; }
    }
}
