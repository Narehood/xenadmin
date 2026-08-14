using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XenAdmin.Actions;
using XenAdmin.CustomFields;
using XenAPI;
using XcpNgCenter.Shell.Services;

namespace XcpNgCenter.Shell.ViewModels;

public partial class CustomFieldValueRow : ViewModelBase
{
    private readonly object? _initialValue;
    private readonly Func<CustomFieldValueRow, System.Threading.Tasks.Task> _remove;

    public CustomFieldValueRow(
        CustomFieldDefinition definition,
        object? value,
        bool isNew,
        Func<CustomFieldValueRow, System.Threading.Tasks.Task> remove)
    {
        Definition = definition;
        IsNew = isNew;
        _initialValue = value;
        _remove = remove;
        ValueText = value switch
        {
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? string.Empty
        };
    }

    public CustomFieldDefinition Definition { get; }
    public string Name => Definition.Name;
    public bool IsDate => Definition.Type == CustomFieldDefinition.Types.Date;
    public bool IsText => !IsDate;
    public bool IsNew { get; }
    public string TypeLabel => IsDate ? "Date and time" : "Text";

    [ObservableProperty]
    private string _valueText = string.Empty;

    public bool HasChanges
    {
        get
        {
            if (!TryGetValue(out var value, out _))
                return true;
            return !ValuesEqual(_initialValue, value);
        }
    }

    public bool TryGetValue(out object? value, out string error)
    {
        if (string.IsNullOrEmpty(ValueText))
        {
            value = null;
            error = string.Empty;
            return true;
        }

        if (IsText)
        {
            value = ValueText;
            error = string.Empty;
            return true;
        }

        if (!DateTime.TryParse(
                ValueText,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var date)
            && !DateTime.TryParse(
                ValueText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out date))
        {
            value = null;
            error = $"Enter a valid date and time for {Name}, or leave it blank.";
            return false;
        }

        value = date.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(date, DateTimeKind.Local)
            : date;
        error = string.Empty;
        return true;
    }

    public CustomField BuildCustomField()
    {
        TryGetValue(out var value, out _);
        return new CustomField(Definition, value);
    }

    [RelayCommand]
    private System.Threading.Tasks.Task Remove() => _remove(this);

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left == null || right == null)
            return left == null && right == null;
        return left.Equals(right);
    }
}

public partial class CustomFieldsEditor : ViewModelBase
{
    private const string TextType = "Text";
    private const string DateType = "Date and time";
    private readonly IXenObject _xenObject;
    private readonly List<CustomFieldDefinition> _addedDefinitions = new();
    private readonly List<CustomFieldDefinition> _removedDefinitions = new();

    public CustomFieldsEditor(IXenObject xenObject)
    {
        _xenObject = xenObject;
        foreach (var definition in CustomFieldsManager.GetCustomFields(xenObject.Connection)
                     .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase))
        {
            var value = CustomFieldsManager.GetCustomFieldValue(xenObject, definition);
            Fields.Add(new CustomFieldValueRow(definition, value, isNew: false, RemoveFieldAsync));
        }
    }

    public ObservableCollection<CustomFieldValueRow> Fields { get; } = new();
    public IReadOnlyList<string> FieldTypes { get; } = [TextType, DateType];
    public bool HasFields => Fields.Count > 0;
    public bool HasNoFields => !HasFields;

    [ObservableProperty]
    private string _newFieldName = string.Empty;

    [ObservableProperty]
    private string _selectedFieldType = TextType;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool HasChanges =>
        _addedDefinitions.Count > 0
        || _removedDefinitions.Count > 0
        || Fields.Any(field => field.HasChanges);

    public bool TryValidate(out string error)
    {
        foreach (var field in Fields)
        {
            if (!field.TryGetValue(out _, out error))
                return false;
        }

        error = string.Empty;
        return true;
    }

    public void AppendActions(List<AsyncAction> actions)
    {
        foreach (var definition in _addedDefinitions)
            actions.Add(new AddCustomFieldAction(_xenObject.Connection, definition));

        var valueChanges = Fields
            .Where(field => field.IsNew ? !string.IsNullOrEmpty(field.ValueText) : field.HasChanges)
            .Select(field => field.BuildCustomField())
            .ToList();
        if (valueChanges.Count > 0)
            actions.Add(new SaveCustomFieldsAction(_xenObject, valueChanges, suppressHistory: true));

        foreach (var definition in _removedDefinitions)
            actions.Add(new RemoveCustomFieldAction(_xenObject.Connection, definition));
    }

    [RelayCommand]
    private void AddField()
    {
        var name = NewFieldName.Trim();
        if (name.Length == 0)
        {
            StatusMessage = "Enter a custom-field name.";
            return;
        }

        var duplicate = Fields.Any(field =>
                            string.Equals(field.Name.Trim(), name, StringComparison.Ordinal))
                        || _removedDefinitions.Any(definition =>
                            string.Equals(definition.Name.Trim(), name, StringComparison.Ordinal));
        if (duplicate)
        {
            StatusMessage = $"A custom field named '{name}' already exists.";
            return;
        }

        var type = string.Equals(SelectedFieldType, DateType, StringComparison.Ordinal)
            ? CustomFieldDefinition.Types.Date
            : CustomFieldDefinition.Types.String;
        var definition = new CustomFieldDefinition(name, type);
        _addedDefinitions.Add(definition);
        Fields.Add(new CustomFieldValueRow(definition, null, isNew: true, RemoveFieldAsync));
        NewFieldName = string.Empty;
        StatusMessage = $"'{name}' will be created when you click OK.";
        NotifyFieldCollectionChanged();
    }

    private async System.Threading.Tasks.Task RemoveFieldAsync(CustomFieldValueRow row)
    {
        if (!row.IsNew)
        {
            var accepted = await ShellConfirmPrompt.ConfirmAsync(new ShellConfirmRequest
            {
                Title = "Delete custom field",
                Message = $"Delete the custom field '{row.Name}' from this pool? Its value will be removed from every object and cannot be recovered.",
                AcceptLabel = "Delete field",
                CancelLabel = "Keep field"
            }).ConfigureAwait(true);
            if (!accepted)
                return;

            _removedDefinitions.Add(row.Definition);
        }
        else
        {
            _addedDefinitions.Remove(row.Definition);
        }

        Fields.Remove(row);
        StatusMessage = row.IsNew
            ? $"Discarded the new field '{row.Name}'."
            : $"'{row.Name}' will be deleted when you click OK.";
        NotifyFieldCollectionChanged();
    }

    private void NotifyFieldCollectionChanged()
    {
        OnPropertyChanged(nameof(HasFields));
        OnPropertyChanged(nameof(HasNoFields));
    }
}
