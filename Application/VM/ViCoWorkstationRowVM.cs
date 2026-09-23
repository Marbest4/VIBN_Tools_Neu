using System.Globalization;
using VIBN_Tools.Core.ViCo;
using VIBN_Tools.GlobalClasses;

namespace VIBN_Tools.Application.VM;

/// <summary>
/// Presentation-only state for one searchable ViCo workstation row. Keeping
/// cell formatting and live availability state out of the search coordinator
/// makes the latter responsible only for loading and actions.
/// </summary>
public sealed class ViCoWorkstationRowVM : MvvmBase
{
	public ViCoWorkstationRowVM(ViCoWorkstation model, IReadOnlyList<string>? matchedColumns = null)
	{
		Model = model;
		MatchedColumns = matchedColumns ?? Array.Empty<string>();
	}

	public ViCoWorkstation Model { get; private set; }
	public string PcName => Model.PcName;
	public string DisplayName => Model.DisplayName;
	public string UserName => Model.UserName;
	public string Status => Model.Status;

	/// <summary>
	/// Keeps the operational state visually scannable without putting WPF
	/// brushes into the view model. Free workstations are green; planning or
	/// active work makes a workstation occupied and therefore red.
	/// </summary>
	public string StatusBackground => Status switch
	{
		"Frei" => "#FFC6EFCE",
		"Belegt" => "#FFFFC7CE",
		_ => "#FFF3F5F7"
	};

	public string ProjectSummary => Model.ProjectSummary;
	public IReadOnlyList<ViCoProjectCardItemVM> PlanningProjects => Model.PlanningProjectCards
		.Select(card => new ViCoProjectCardItemVM(card))
		.Concat(Model.PlanningProjectCards.Count == 0
			? Model.PlanningProjects.Select(title => new ViCoProjectCardItemVM(title, "Planung"))
			: Array.Empty<ViCoProjectCardItemVM>())
		.ToArray();
	public IReadOnlyList<ViCoProjectCardItemVM> WorkingProjects => Model.WorkingProjectCards
		.Select(card => new ViCoProjectCardItemVM(card))
		.Concat(Model.WorkingProjectCards.Count == 0
			? Model.WorkingProjects.Select(title => new ViCoProjectCardItemVM(title, "In Arbeit"))
			: Array.Empty<ViCoProjectCardItemVM>())
		.ToArray();
	public string PlanningProjectSummary => string.Join(" | ", Model.PlanningProjects);
	public string WorkingProjectSummary => string.Join(" | ", Model.WorkingProjects);
	public bool UseCollapsedActiveProjectPresentation =>
		string.Equals(PcName, "Angelegt (Tool)", StringComparison.OrdinalIgnoreCase);
	public string PlanningProjectHeader => FormatProjectHeader(PlanningProjects.Count);
	public string WorkingProjectHeader => FormatProjectHeader(WorkingProjects.Count);
	public string PlanningStartSummary => FormatDates(Model.PlanningProjectCards, card => card.StartDate);
	public string PlanningEndSummary => FormatDates(Model.PlanningProjectCards, card => card.Deadline);
	public string WorkingStartSummary => FormatDates(Model.WorkingProjectCards, card => card.StartDate);
	public string WorkingEndSummary => FormatDates(Model.WorkingProjectCards, card => card.Deadline);
	public IReadOnlyList<string> MatchedColumns { get; }
	public string SearchMatchSummary => MatchedColumns.Count == 0
		? string.Empty
		: $"Suchtreffer in: {string.Join(", ", MatchedColumns)}";
	public bool HasActiveProjects => Model.HasActiveProjects;
	public string AdditionalProjects => Model.AdditionalProjects;
	public IReadOnlyList<string> CompletedProjects => Model.CompletedProjects;
	public string CompletedProjectHeader => Model.CompletedProjects.Count switch
	{
		0 => "Keine",
		1 => "1 Projekt",
		var count => $"{count} Projekte"
	};
	public string SoftwareInformation => Model.SoftwareInformation;
	public IReadOnlyList<AutomationSoftwareInfo> SoftwareDetails => Model.AutomationSoftware;
	public string FeeInformation => Model.FeeInformation;
	public string HardwareInformation => Model.HardwareInformation;
	public int RobotCount => Model.RobotCount;
	public string RobotSummary => Model.RobotSummary;
	public IReadOnlyList<string> Details => Model.Details;
	public IReadOnlyList<string> RelevantKanbanizeDetails => Model.Details
		.Where(detail => !detail.StartsWith("Robot:", StringComparison.OrdinalIgnoreCase) &&
						 !detail.StartsWith("KONFIGURATION", StringComparison.OrdinalIgnoreCase))
		.ToArray();
	public string ConfigurationSoftware => Model.WorkstationConfiguration.Software.Value;
	public string ConfigurationLocation => Model.WorkstationConfiguration.Location.Value;
	public string ConfigurationProjectIp => Model.WorkstationConfiguration.ProjectIp.Value;
	public string ConfigurationOther => Model.WorkstationConfiguration.Other.Value;
	public string ConfigurationStatus => Model.HasConfigurationCard
		? "Vorhanden"
		: "Konfigurationskarte fehlt!";
	public string ConfigurationStatusBackground => Model.HasConfigurationCard
		? "#FFC6EFCE"
		: "#FFFFC7CE";

	private bool _isOnline;

	/// <summary>Used by the view to disable only actions that actually require the workstation.</summary>
	public bool IsOnline
	{
		get => _isOnline;
		private set
		{
			_isOnline = value;
			OnPropertyChanged();
		}
	}

	private string _onlineStatus = "Wird geprüft …";
	public string OnlineStatus
	{
		get => _onlineStatus;
		private set
		{
			_onlineStatus = value;
			OnPropertyChanged();
		}
	}

	private string _onlineStatusBackground = "#FFF3F5F7";
	public string OnlineStatusBackground
	{
		get => _onlineStatusBackground;
		private set
		{
			_onlineStatusBackground = value;
			OnPropertyChanged();
		}
	}

	/// <summary>Updates the compact availability cell without putting WPF types into the view model.</summary>
	public void SetOnline(bool isOnline)
	{
		IsOnline = isOnline;
		OnlineStatus = isOnline ? "Online" : "Offline";
		OnlineStatusBackground = isOnline ? "#FFC6EFCE" : "#FFFFC7CE";
		if (!isOnline)
			SetRemoteSessionOffline();
	}

	private string _remoteSessionStatus = "Wird geprüft …";
	public string RemoteSessionStatus
	{
		get => _remoteSessionStatus;
		private set
		{
			_remoteSessionStatus = value;
			OnPropertyChanged();
		}
	}

	private string _lastRemoteLogon = "Wird geprüft …";
	public string LastRemoteLogon
	{
		get => _lastRemoteLogon;
		private set
		{
			_lastRemoteLogon = value;
			OnPropertyChanged();
		}
	}

	private string _remoteSessionDiagnostic = string.Empty;
	public string RemoteSessionDiagnostic
	{
		get => _remoteSessionDiagnostic;
		private set
		{
			_remoteSessionDiagnostic = value;
			OnPropertyChanged();
		}
	}

	/// <summary>Maps a remote query result to concise, user-facing grid values.</summary>
	public void SetRemoteSession(ViCoRemoteSessionInfo info)
	{
		if (!info.IsAvailable)
		{
			RemoteSessionDiagnostic = info.DiagnosticMessage;
			var permissionFailure = info.DiagnosticMessage.Contains("Berechtigung", StringComparison.OrdinalIgnoreCase) ||
									info.DiagnosticMessage.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);
			RemoteSessionStatus = permissionFailure ? "Nicht abrufbar (Rechte)" : "Nicht abrufbar";
			LastRemoteLogon = permissionFailure ? "Nicht abrufbar (Rechte)" : "Nicht abrufbar";
			return;
		}

		RemoteSessionDiagnostic = string.Empty;

		RemoteSessionStatus = string.IsNullOrWhiteSpace(info.ActiveUser)
			? "Keine aktive Sitzung"
			: $"Aktiv: {info.ActiveUser}";
		LastRemoteLogon = info.LastLogonAt is null
			? "Keine Anmeldung gefunden"
			: $"{info.LastLogonUser} – {info.LastLogonAt.Value.LocalDateTime:dd.MM.yyyy HH:mm}";
	}

	private void SetRemoteSessionOffline()
	{
		RemoteSessionStatus = "Offline";
		LastRemoteLogon = "—";
		RemoteSessionDiagnostic = "Der PC ist offline.";
	}

	/// <summary>Updates only the editable KONFIGURATION projection after a successful save.</summary>
	public void UpdateConfiguration(ViCoWorkstationConfiguration configuration)
	{
		var userName = string.IsNullOrWhiteSpace(configuration.User.Value)
			? Model.UserName
			: configuration.User.Value.Trim();
		Model = Model with { UserName = userName, Configuration = configuration };
		OnPropertyChanged(nameof(UserName));
		OnPropertyChanged(nameof(ConfigurationSoftware));
		OnPropertyChanged(nameof(ConfigurationLocation));
		OnPropertyChanged(nameof(ConfigurationProjectIp));
		OnPropertyChanged(nameof(ConfigurationOther));
		OnPropertyChanged(nameof(ConfigurationStatus));
		OnPropertyChanged(nameof(ConfigurationStatusBackground));
	}

	private static string FormatDates(
		IEnumerable<ViCoProjectCardInfo> cards,
		Func<ViCoProjectCardInfo, DateTimeOffset?> selectDate) =>
		string.Join(" | ", cards
			.Where(card => card.Status is "Planung" or "In Arbeit")
			.Select(selectDate)
			.Where(date => date is not null)
			.Select(date => date!.Value.LocalDateTime.ToString("dd.MM.yyyy")));

	private static string FormatProjectHeader(int count) => count switch
	{
		0 => "Keine",
		1 => "1 Projekt",
		_ => $"{count} Projekte"
	};

	public string WorkingEndBackground
	{
		get
		{
			if (string.IsNullOrWhiteSpace(WorkingEndSummary))
				return "#FFFFFFFF";

			var dates = WorkingEndSummary
				.Split('|', StringSplitOptions.RemoveEmptyEntries)
				.Select(x => x.Trim());

			var today = DateTime.Today;
			var red = false;
			var yellow = false;

			foreach (var dateString in dates)
			{
				if (!DateTime.TryParseExact(
						dateString,
						"dd.MM.yyyy",
						CultureInfo.InvariantCulture,
						DateTimeStyles.None,
						out var endDate))
					continue;

				if (endDate.Date < today)
				{
					red = true;
					break;
				}

				if (endDate.Date <= today.AddDays(7))
				{
					yellow = true;
				}
			}

			if (red)
				return "#FFFFC7CE";   // Rot

			if (yellow)
				return "#FFFFEB9C";   // Gelb

			return "#FFFFFFFF";       // Weiß
		}
	}
}

public sealed record ViCoProjectCardItemVM(int CardId, string Title, string Status, string Start, string End)
{
	public ViCoProjectCardItemVM(ViCoProjectCardInfo card)
		: this(
			card.CardId,
			ProjectIdentity.CleanDisplay(card.Title),
			card.Status,
			Format(card.StartDate),
			Format(card.Deadline))
	{
	}

	public ViCoProjectCardItemVM(string title, string status)
		: this(0, ProjectIdentity.CleanDisplay(title), status, "nicht angegeben", "nicht angegeben")
	{
	}

	public bool CanOpenCard => CardId > 0;
	public string DateSummary => $"Start: {Start}; Ende: {End}";

	private static string Format(DateTimeOffset? value) =>
		value is null ? "nicht angegeben" : value.Value.LocalDateTime.ToString("dd.MM.yyyy");
}

public sealed class ViCoColumnOptionVM : MvvmBase
{
	private readonly Action _changed;
	private bool _isVisible;

	public ViCoColumnOptionVM(string key, string title, bool isVisible, Action changed)
	{
		Key = key;
		Title = title;
		_isVisible = isVisible;
		_changed = changed;
	}

	public string Key { get; }
	public string Title { get; }

	public bool IsVisible
	{
		get => _isVisible;
		set
		{
			if (_isVisible == value)
				return;
			_isVisible = value;
			OnPropertyChanged();
			_changed();
		}
	}

	public void Apply(bool value)
	{
		if (_isVisible == value)
			return;
		_isVisible = value;
		OnPropertyChanged(nameof(IsVisible));
	}
}

/// <summary>
/// Editable presentation state for one existing KONFIGURATION subtask. It
/// tracks its original value so the UI never sends unchanged board fields.
/// </summary>
public sealed class ViCoConfigurationFieldVM : MvvmBase
{
	private string _value;
	private string _originalValue;
	private bool _existsOnBoard;

	public ViCoConfigurationFieldVM(ViCoConfigurationField field)
	{
		Key = field.Key;
		SubtaskId = field.SubtaskId;
		_value = field.Value;
		_originalValue = field.Value;
		_existsOnBoard = field.SubtaskId > 0;
	}

	public string Key { get; }

	public int SubtaskId { get; }

	public bool CanSave => _existsOnBoard;

	public string Value
	{
		get => _value;
		set
		{
			if (string.Equals(_value, value, StringComparison.Ordinal))
				return;
			_value = value ?? string.Empty;
			OnPropertyChanged();
			OnPropertyChanged(nameof(IsChanged));
		}
	}

	public bool IsChanged => !string.Equals(_originalValue, Value, StringComparison.Ordinal);

	public ViCoConfigurationField ToField() => new(Key, Value, SubtaskId);

	public void AcceptSavedValue()
	{
		_originalValue = Value;
		_existsOnBoard = true;
		OnPropertyChanged(nameof(IsChanged));
		OnPropertyChanged(nameof(CanSave));
	}

}
