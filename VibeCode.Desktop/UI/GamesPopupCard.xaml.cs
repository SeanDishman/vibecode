using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VibeCode.UI;

public partial class GamesPopupCard : UserControl
{
    public GamesPopupCard() => InitializeComponent();

    /// <summary>Raised with the game definition so the host can open the right window.</summary>
    internal event Action<GameDefinition>? OpenGameRequested;

    private void OnOpenSurviveTheShapes(object sender, RoutedEventArgs e) =>
        OpenGameRequested?.Invoke(GameCatalog.SurviveTheShapes);

    private void OnOpenTowerDefense(object sender, RoutedEventArgs e) =>
        OpenGameRequested?.Invoke(GameCatalog.TowerDefense);

    private void OnOpenTinyEmpires(object sender, RoutedEventArgs e) =>
        OpenGameRequested?.Invoke(GameCatalog.TinyEmpires);

    private void OnOpenFishingTycoon(object sender, RoutedEventArgs e) =>
        OpenGameRequested?.Invoke(GameCatalog.FishingTycoon);

    internal static void AttachTo(VibeCode.MainWindow owner)
    {
        if (owner.FindName("GamesPopup") is not Popup popup || popup.Child is GamesPopupCard) return;

        var card = new GamesPopupCard();
        card.OpenGameRequested += game =>
        {
            popup.IsOpen = false;
            GameWindow.Open(owner, game);
        };
        popup.Child = card;
    }
}
