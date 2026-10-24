using Avalonia.Controls;
using Avalonia.Input;

namespace PoorMansFeed;

/// <summary>
/// A DockPanel, so Settings can sit at the foot, that answers every direction exactly as a vertical StackPanel with
/// as many children does (a test asks one), because without it the arrow keys stop moving the selection.
/// </summary>
public sealed class NavItemsPanel : DockPanel, INavigableContainer
{
    IInputElement? INavigableContainer.GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        IInputElement? result = InDirection(direction, from as Control);
        if (result is null && wrap)
        {
            result = direction switch
            {
                NavigationDirection.Up or NavigationDirection.Previous or NavigationDirection.PageUp
                    => InDirection(NavigationDirection.Last, null),
                NavigationDirection.Down or NavigationDirection.Next or NavigationDirection.PageDown
                    => InDirection(NavigationDirection.First, null),
                _ => null,
            };
        }

        return result;
    }

    // The child one step from the element without wrapping; -1 stands for an element that isn't a direct child
    private Control? InDirection(NavigationDirection direction, Control? from)
    {
        int index = from is null ? -1 : Children.IndexOf(from);
        index = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => Children.Count - 1,
            NavigationDirection.Next => index + 1,
            NavigationDirection.Previous => index - 1,
            NavigationDirection.Up => index < 0 ? -1 : index - 1,
            NavigationDirection.Down => index < 0 ? -1 : index + 1,
            _ => -1,
        };

        return index >= 0 && index < Children.Count ? Children[index] : null;
    }
}
