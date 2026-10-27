namespace VIBN_Tools.Core.ViCo;

public enum ViCoProjectDocumentsStatus { Available, Empty, Missing, Unavailable }

public sealed record ViCoProjectDocumentsResult(string FolderPath, IReadOnlyList<string> Files,
    ViCoProjectDocumentsStatus Status, string Detail = "")
{
    public string DisplayText => Status switch
    {
        ViCoProjectDocumentsStatus.Available => string.Join(Environment.NewLine, Files),
        ViCoProjectDocumentsStatus.Empty => "Ordner leer",
        ViCoProjectDocumentsStatus.Missing => "Ordner nicht vorhanden",
        _ => "Ordner nicht erreichbar"
    };
}

public interface IViCoProjectDocumentsService
{
    Task<ViCoProjectDocumentsResult> ReadAsync(string simulationPath, CancellationToken cancellationToken = default);
}
