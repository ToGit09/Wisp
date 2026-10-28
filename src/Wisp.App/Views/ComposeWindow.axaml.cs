using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Wisp.App.ViewModels;

namespace Wisp.App.Views;

public partial class ComposeWindow : Window
{
    public static readonly IValueConverter FileName = new FuncValueConverter<string?, string>(p => Path.GetFileName(p ?? ""));

    readonly ComposeViewModel _vm;
    bool _closingConfirmed;

    public ComposeWindow() : this(null!) { }

    public ComposeWindow(ComposeViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        if (vm is null) return;

        vm.Sent += () =>
        {
            _closingConfirmed = true;
            Close();
        };
        AttachButton.Click += async (_, _) => await PickAttachmentsAsync();
        DiscardButton.Click += (_, _) => Close();
        Closing += async (_, e) =>
        {
            if (_closingConfirmed || !vm.IsDirty) return;
            e.Cancel = true;
            if (await ConfirmDialog.ShowAsync(this, "Entwurf verwerfen?", "Die Nachricht wurde noch nicht gesendet und geht verloren.", "Verwerfen", true))
            {
                _closingConfirmed = true;
                Close();
            }
        };

        // Drag & drop files to attach them.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files is not null) vm.AddAttachments(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        });

        Opened += (_, _) =>
        {
            if (string.IsNullOrEmpty(vm.To)) return;
            BodyBox.Focus();
            BodyBox.CaretIndex = 0; // replies: start typing above the quote
        };
    }

    async Task PickAttachmentsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Dateien anhängen", AllowMultiple = true });
        _vm.AddAttachments(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }
}
