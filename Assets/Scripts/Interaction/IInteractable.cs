namespace ParkShift.Interaction
{
    public interface IInteractable
    {
        /// <summary>Prompt shown when looked at; null/empty hides it.</summary>
        string GetPrompt(Player.RoleController role);
        void Interact(Player.RoleController role);
    }
}
