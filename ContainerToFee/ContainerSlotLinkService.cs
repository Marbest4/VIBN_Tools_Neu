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
        CancellationToken cancellationToken = default,
        IReadOnlyList<(Guid ObjectGuid, string[] SlotNames)>? knownAssignments = null)
    {
        if (objectGuid == Guid.Empty || variableGuid == Guid.Empty || string.IsNullOrWhiteSpace(slotName))
            throw new ArgumentException("Eine Variablenzuordnung enthält einen ungültigen Endpunkt.");

        cancellationToken.ThrowIfCancellationRequested();
        var existing = knownAssignments ?? (await Services.ApiInstance.Interface.GetAssignedSceneObjectsAsync(variableGuid) ?? []).ToArray();
        if (ContainsVariableEndpoint(existing, objectGuid, slotName)) return;

        cancellationToken.ThrowIfCancellationRequested();
        var sendAccepted = await Services.ApiInstance.Interface.SendSlotVarAssignmentAsync(
            objectGuid,
            slotName,
            variableGuid,
            true);

        var attempts = sendAccepted ? VerificationAttempts : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assignments = await Services.ApiInstance.Interface.GetAssignedSceneObjectsAsync(variableGuid);
            var previousPreserved = (existing ?? []).All(endpoint => (endpoint.Item2 ?? []).All(slot =>
                ContainsVariableEndpoint(assignments, endpoint.Item1, slot)));
            if (ContainsVariableEndpoint(assignments, objectGuid, slotName) && previousPreserved)
                return;

            if (attempt + 1 < attempts)
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
        await AssignAndVerifyCoreAsync(endpoints, context,
            async (guid, slot) => (await Services.ApiInstance.Interface.GetSlotSlotAssignmentAsync(guid, slot) ?? []).ToArray(),
            (first, firstSlot, second, secondSlot) => Services.ApiInstance.Interface.SendSlotSlotAssignmentAsync(first, firstSlot, second, secondSlot),
            (guids, slots) => Services.ApiInstance.Interface.SendMultipleSlotSlotAssignmentsAsync(guids, slots),
            cancellationToken);
    }

    internal static async Task AssignAndVerifyCoreAsync(
        IReadOnlyList<(Guid ObjectGuid, string SlotName)> endpoints,
        string context,
        Func<Guid, string, Task<IReadOnlyList<(string ObjectGuid, string[] SlotNames)>>> read,
        Func<Guid, string, Guid, string, Task<bool>> sendPair,
        Func<Guid[], string[], Task<bool>> sendGroup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count < 2)
            throw new ArgumentException("Eine Slotverknüpfung benötigt mindestens zwei Endpunkte.", nameof(endpoints));
        if (endpoints.Any(endpoint => endpoint.ObjectGuid == Guid.Empty || string.IsNullOrWhiteSpace(endpoint.SlotName)))
            throw new ArgumentException("Eine Slotverknüpfung enthält einen ungültigen Endpunkt.", nameof(endpoints));

        var anchor = endpoints[0];
        var expectedLinks = endpoints.Skip(1).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var responses = new Dictionary<(Guid, string), IReadOnlyList<(string ObjectGuid, string[] SlotNames)>>();
        var existing = await read(anchor.ObjectGuid, anchor.SlotName);
        responses[(anchor.ObjectGuid, anchor.SlotName.ToUpperInvariant())] = existing;
        async Task<bool> IsConfirmed((Guid ObjectGuid, string SlotName) endpoint)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ContainsAllEndpoints(responses[(anchor.ObjectGuid, anchor.SlotName.ToUpperInvariant())], [endpoint])) return true;
            var key = (endpoint.ObjectGuid, endpoint.SlotName.ToUpperInvariant());
            if (!responses.TryGetValue(key, out var reverse))
            {
                reverse = await read(endpoint.ObjectGuid, endpoint.SlotName);
                responses[key] = reverse;
            }
            return ContainsAllEndpoints(reverse, [anchor]);
        }
        var missing = new List<(Guid ObjectGuid, string SlotName)>();
        foreach (var endpoint in expectedLinks)
            if (!await IsConfirmed(endpoint)) missing.Add(endpoint);
        if (missing.Count == 0) return;
        var retainedLinks = (existing ?? []).SelectMany(link => Guid.TryParse(link.Item1, out var guid)
                ? (link.Item2 ?? []).Select(slot => (ObjectGuid: guid, SlotName: slot)) : [])
            .Where(endpoint => endpoint != anchor).ToArray();
        // Use the original point-to-point SDK call for a pair. Group writes are
        // reserved for actual fan-out, and include existing scene endpoints.
        var writes = new[] { anchor }.Concat(expectedLinks).Concat(retainedLinks).Distinct(new SlotEndpointComparer()).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var sendAccepted = writes.Length == 2
            ? await sendPair(anchor.ObjectGuid, anchor.SlotName, expectedLinks[0].ObjectGuid, expectedLinks[0].SlotName)
            : await sendGroup(writes.Select(endpoint => endpoint.ObjectGuid).ToArray(), writes.Select(endpoint => endpoint.SlotName).ToArray());
        var attempts = sendAccepted ? VerificationAttempts : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            responses.Clear();
            responses[(anchor.ObjectGuid, anchor.SlotName.ToUpperInvariant())] = await read(anchor.ObjectGuid, anchor.SlotName);
            var confirmed = true;
            foreach (var endpoint in expectedLinks.Concat(retainedLinks).Distinct(new SlotEndpointComparer()))
                if (!await IsConfirmed(endpoint)) { confirmed = false; break; }
            if (confirmed) return;

            if (attempt + 1 < attempts)
                await Task.Delay(VerificationDelay, cancellationToken);
        }

        var expectedText = string.Join(", ", expectedLinks.Select(FormatEndpoint));
        var actualText = string.Join("; ", responses.Select(response =>
            $"{response.Key.Item1:D}/{response.Key.Item2} → " + string.Join(", ", response.Value.SelectMany(link =>
                (link.SlotNames ?? []).Select(slot => $"{link.ObjectGuid}/{slot}")))));
        throw new InvalidOperationException(
            $"FEE hat die Slotverknüpfung für '{context}' nicht vollständig bestätigt " +
            $"(Start {FormatEndpoint(anchor)}, erwartet {expectedText}, " +
            $"SDK-Rückgabe: {sendAccepted}; tatsächlich gelesen: {actualText}).");
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
