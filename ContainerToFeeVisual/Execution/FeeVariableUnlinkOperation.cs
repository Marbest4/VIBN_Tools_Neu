using System.Reflection;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.GlobalClasses;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Only explicit SDK remove operations qualify; send flags are never guessed.</summary>
internal sealed record FeeVariableUnlinkOperation(Guid ObjectGuid, string Slot, Guid VariableGuid,
    MethodInfo Method, object[] Arguments)
{
    public static FeeVariableUnlinkOperation Prepare(Guid objectGuid, string slot, Guid variableGuid)
    {
        var api = Services.ApiInstance.Interface;
        var names = new[] { "RemoveSlotVarAssignmentAsync", "DeleteSlotVarAssignmentAsync", "RemoveSlotVariableAssignmentAsync",
            "DeleteSlotVariableAssignmentAsync", "ClearSlotAssignmentAsync", "RemoveSlotVarAssignment", "DeleteSlotVarAssignment" };
        foreach (var method in api.GetType().GetMethods().Where(method => names.Contains(method.Name, StringComparer.Ordinal)))
        {
            var parameters = method.GetParameters();
            if (!parameters.Any(parameter => parameter.ParameterType == typeof(Guid)) ||
                !parameters.Any(parameter => parameter.ParameterType == typeof(string))) continue;
            var arguments = new List<object>(); var valid = true;
            foreach (var parameter in parameters)
            {
                var name = parameter.Name?.ToLowerInvariant() ?? "";
                if (parameter.ParameterType == typeof(string) && name.Contains("slot")) arguments.Add(slot);
                else if (parameter.ParameterType == typeof(Guid) && (name.Contains("var") || name.Contains("signal"))) arguments.Add(variableGuid);
                else if (parameter.ParameterType == typeof(Guid) && (name.Contains("object") || name.Contains("scene"))) arguments.Add(objectGuid);
                else if (parameter.HasDefaultValue) arguments.Add(parameter.DefaultValue!);
                else { valid = false; break; }
            }
            if (valid) return new FeeVariableUnlinkOperation(objectGuid, slot, variableGuid, method, arguments.ToArray());
        }
        throw new InvalidOperationException($"Das verbundene FEE-SDK stellt keine eindeutig unterstützte Löschoperation für " +
            $"die entfallene Variablenzuordnung {objectGuid:D}/{slot} bereit. Es wurden noch keine FEE-Änderungen geschrieben.");
    }

    public async Task ExecuteAsync()
    {
        var result = Method.Invoke(Services.ApiInstance.Interface, Arguments);
        if (result is Task task) { await task; result = task.GetType().GetProperty("Result")?.GetValue(task); }
        if (result is false) throw new InvalidOperationException("FEE hat das Entfernen einer Variablenzuordnung abgelehnt.");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var assigned = await Services.ApiInstance.Interface.GetAssignedSceneObjectsAsync(VariableGuid);
            if (!ContainerSlotLinkService.ContainsVariableEndpoint(assigned, ObjectGuid, Slot)) return;
            await Task.Delay(120);
        }
        throw new InvalidOperationException($"FEE hat das Entfernen von {ObjectGuid:D}/{Slot} nicht bestätigt.");
    }
}
