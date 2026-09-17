using System;

namespace Sentrychan.UI.Interfaces;

public interface IReactionOverlayService
{
    void ShowReaction(string? username, string reactionType);
}
