using System.Threading.Tasks;
using Cocorra.DAL.DTOS.ChatDto;

namespace Cocorra.BLL.Services.ChatService
{
    /// <summary>
    /// Delivers a saved 1-on-1 message on every hub the two users may be listening on, whichever
    /// surface it was sent from. Delivery used to follow the sender's hub, so a message sent from
    /// a room never reached the receiver's chat screen, and a reply sent from the chat screen
    /// never reached a user still inside the room.
    /// </summary>
    public interface IDirectMessageNotifier
    {
        Task DeliverAsync(MessageDto message);
    }

    /// <summary>Where a message was sent from. Null ids mean the regular chat screen.</summary>
    public sealed record MessageOrigin(System.Guid? RoomId = null, System.Guid? TextRoomId = null)
    {
        public static readonly MessageOrigin Direct = new();
    }
}
