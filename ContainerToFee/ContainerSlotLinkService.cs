using FS.SDK.Scene.Objects;
using FS.SDK.Components;
using FS.SDK.Utilities;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFee;

/// <summary>
/// Writes and verifies the slot links required by generated FEE objects.
/// The SDK can reject a link without throwing (for example when an optional
/// object slot is disabled), so a completed send alone is not sufficient.
/// </summary>
public static class ContainerSlotLinkService
{
    private const int VerificationAttempts = 5;
    private static readonly TimeSpan VerificationDelay = TimeSpan.FromMilliseconds(120);

    public static async Task<bool> AssignAndVerifyAsync(
        Guid firstObjectGuid,
        string firstSlotName,
        Guid secondObjectGuid,
        string secondSlotName,
        string context,
        CancellationToken cancellationToken = default)
    {
        await AssignAndVerifyAsync(
            [
                (firstObjectGuid, firstSlotName),
                (secondObjectGuid, secondSlotName),
            ],
            context,
            cancellationToken);
        return true;
    }

    public static async Task AssignVariableAndVerifyAsync(
        Guid objectGuid,
        string slotName,
        Guid variableGuid,
        string context,
        CancellationToken cancellationToken = default)
    {
        if (objectGuid == Guid.Empty || variableGuid == Guid.Empty || string.IsNullOrWhiteSpace(slotName))
            throw new ArgumentException("Eine Variablenzuordnung enthält einen ungültigen Endpunkt.");

        cancellationToken.ThrowIfCancellationRequested();
        var existing = await Services.ApiInstance.Interface.GetAssignedSceneObjectsAsync(variableGuid);
        if (ContainsVariableEndpoint(existing, objectGuid, slotName)) return;

        var sendAccepted = await Services.ApiInstance.Interface.SendSlotVarAssignmentAsync(
            objectGuid,
            slotName,
            variableGuid,
            true);

        for (var attempt = 0; attempt < VerificationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assignments = await Services.ApiInstance.Interface.GetAssignedSceneObjectsAsync(variableGuid);
            var previousPreserved = (existing ?? []).All(endpoint => (endpoint.Item2 ?? []).All(slot =>
                ContainsVariableEndpoint(assignments, endpoint.Item1, slot)));
            if (ContainsVariableEndpoint(assignments, objectGuid, slotName) && previousPreserved)
                return;

            if (attempt + 1 < VerificationAttempts)
                await Task.Delay(VerificationDelay, cancellationToken);
        }

        throw new InvalidOperationException(
            $"FEE hat die Variablenzuordnung für '{context}' nicht bestätigt " +
            $"(Ziel {objectGuid:D}/{slotName}, Variable {variableGuid:D}, " +
            $"SDK-Rückgabe: {sendAccepted}).");
    }

    public static async Task AssignAndVerifyAsync(
        IReadOnlyList<(Guid ObjectGuid, string SlotName)> endpoints,
        string context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count < 2)
            throw new ArgumentException("Eine Slotverknüpfung benötigt mindestens zwei Endpunkte.", nameof(endpoints));
        if (endpoints.Any(endpoint => endpoint.ObjectGuid == Guid.Empty || string.IsNullOrWhiteSpace(endpoint.SlotName)))
            throw new ArgumentException("Eine Slotverknüpfung enthält einen ungültigen Endpunkt.", nameof(endpoints));

        var anchor = endpoints[0];
        var expectedLinks = endpoints.Skip(1).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var existing = await Services.ApiInstance.Interface.GetSlotSlotAssignmentAsync(anchor.ObjectGuid, anchor.SlotName);
        var missing = expectedLinks.Where(endpoint => !ContainsAllEndpoints(existing, [endpoint])).ToArray();
        if (missing.Length == 0) return;
        var retainedLinks = (existing ?? []).SelectMany(link => Guid.TryParse(link.Item1, out var guid)
                ? (link.Item2 ?? []).Select(slot => (ObjectGuid: guid, SlotName: slot)) : [])
            .Where(endpoint => endpoint != anchor).ToArray();
        var writes = new[] { anchor }.Concat(missing).ToArray();
        var sendAccepted = await Services.ApiInstance.Interface.SendMultipleSlotSlotAssignmentsAsync(
            writes.Select(endpoint => endpoint.ObjectGuid).ToArray(),
            writes.Select(endpoint => endpoint.SlotName).ToArray());
        for (var attempt = 0; attempt < VerificationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actualLinks = await Services.ApiInstance.Interface.GetSlotSlotAssignmentAsync(
                anchor.ObjectGuid,
                anchor.SlotName);
            if (ContainsAllEndpoints(actualLinks, expectedLinks) && ContainsAllEndpoints(actualLinks, retainedLinks))
                return;

            if (attempt + 1 < VerificationAttempts)
                await Task.Delay(VerificationDelay, cancellationToken);
        }

        var expectedText = string.Join(", ", expectedLinks.Select(FormatEndpoint));
        throw new InvalidOperationException(
            $"FEE hat die Slotverknüpfung für '{context}' nicht vollständig bestätigt " +
            $"(Start {FormatEndpoint(anchor)}, erwartet {expectedText}, " +
            $"SDK-Rückgabe: {sendAccepted}).");
    }

    public static async Task EnsureFloorCollisionSlotEnabledAsync(
        FeeFloor floor,
        string context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(floor);
        if (floor.Guid == Guid.Empty)
            throw new InvalidOperationException($"Floor für '{context}' besitzt keine gültige GUID.");

        // CreateObject with an existing GUID opens an SDK update transaction;
        // it does not create a second Floor. This is required for both freshly
        // generated and manually selected existing Floors.
        Services.ApiInstance.Object.CreateObject(nameof(Floor), floor.Guid);
        await Services.ApiInstance.Object.SetPropertyAsync(
            floor.Guid,
            nameof(Floor.CollisionSlot),
            true);
        await Services.ApiInstance.Object.SendAndWait(floor.Guid);

        for (var attempt = 0; attempt < VerificationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var valueXml = await Services.ApiInstance.Object.GetPropertyAsync(
                floor.Guid,
                nameof(Floor.CollisionSlot));
            if (Services.ApiInstance.XmlHelper.ConvertToBool(valueXml))
            {
                floor.UseCollisionSlot = true;
                return;
            }

            if (attempt + 1 < VerificationAttempts)
                await Task.Delay(VerificationDelay, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Der Collision-Slot des Floors '{floor.Name}' für '{context}' konnte nicht aktiviert werden.");
    }

    public static async Task EnsurePositionControlAsync(FeeJoint joint, CancellationToken cancellationToken = default)
    {
        if (joint.ControlType == MotionSource.Position) return;
        cancellationToken.ThrowIfCancellationRequested();
        Services.ApiInstance.Object.CreateObject(nameof(MotionJoint), joint.Guid);
        await Services.ApiInstance.Object.SetPropertyAsync(joint.Guid, nameof(JointControllerComponent.MotionSource), MotionSource.Position, "Controller");
        await Services.ApiInstance.Object.SendAndWait(joint.Guid);
        var value = await Services.ApiInstance.Object.GetPropertyAsync(joint.Guid, nameof(JointControllerComponent.MotionSource), "Controller");
        if (!string.Equals(Services.ApiInstance.XmlHelper.ConvertToString(value), "Position", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Positionssteuerung von '{joint.Name}' wurde von FEE nicht bestätigt.");
        joint.ControlType = MotionSource.Position;
    }

    public static bool ContainsAllEndpoints(
        IEnumerable<(string ObjectGuid, string[] SlotNames)> actualLinks,
        IEnumerable<(Guid ObjectGuid, string SlotName)> expectedLinks)
    {
        var actual = (actualLinks ?? [])
            .SelectMany(link => (link.SlotNames ?? [])
                .Select(slotName => (link.ObjectGuid, SlotName: slotName)))
            .Where(link => Guid.TryParse(link.ObjectGuid, out _))
            .Select(link => (ObjectGuid: Guid.Parse(link.ObjectGuid), link.SlotName))
            .ToHashSet(new SlotEndpointComparer());

        return expectedLinks.All(expected => actual.Contains(expected));
    }

    public static bool ContainsVariableEndpoint(
        IEnumerable<(Guid ObjectGuid, string[] SlotNames)> actualAssignments,
        Guid expectedObjectGuid,
        string expectedSlotName) =>
        (actualAssignments ?? []).Any(assignment =>
            assignment.ObjectGuid == expectedObjectGuid &&
            (assignment.SlotNames ?? []).Any(slotName => string.Equals(
                slotName,
                expectedSlotName,
                StringComparison.OrdinalIgnoreCase)));

    private static string FormatEndpoint((Guid ObjectGuid, string SlotName) endpoint) =>
        $"{endpoint.ObjectGuid:D}/{endpoint.SlotName}";

    private sealed class SlotEndpointComparer : IEqualityComparer<(Guid ObjectGuid, string SlotName)>
    {
        public bool Equals(
            (Guid ObjectGuid, string SlotName) left,
            (Guid ObjectGuid, string SlotName) right) =>
            left.ObjectGuid == right.ObjectGuid &&
            string.Equals(left.SlotName, right.SlotName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid ObjectGuid, string SlotName) endpoint) =>
            HashCode.Combine(
                endpoint.ObjectGuid,
                StringComparer.OrdinalIgnoreCase.GetHashCode(endpoint.SlotName ?? string.Empty));
    }
}
