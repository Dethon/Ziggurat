using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IStreamResumeService
{
    // toldOfATurn: the hub said a turn started on it, so a page that finds nothing left to follow
    // missed that turn and reads it from the history instead.
    Task TryResumeStreamAsync(StoredTopic topic, bool toldOfATurn = false);
}