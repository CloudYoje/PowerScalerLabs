using System;
using System.Collections.Generic;
using System.Linq;

namespace PowerScalerLabs.App.Overlay;

internal sealed record ExperimentTestDefinition(
    string Category,
    string Name,
    string Instruction,
    string ExpectedDirection,
    string IsolationTip);

internal sealed record ExperimentCategoryDefinition(
    string Name,
    IReadOnlyList<ExperimentTestDefinition> Tests);

internal static class ExperimentCatalog
{
    internal static IReadOnlyList<ExperimentCategoryDefinition> Categories { get; } =
    [
        Category("Resources",
            Test("Idle / Stable", "Stand completely still without attacking, guarding, charging, or taking damage.", "Most true resource values should remain stable.", "Use this to measure background noise before other tests."),
            Test("Spend Ki", "Use exactly one skill that consumes Ki, then compare immediately.", "Current Ki and cost-related values should decrease.", "Avoid taking damage or spending stamina during the test."),
            Test("Regenerate Ki", "Charge Ki or allow a known Ki-regeneration effect to raise the gauge.", "Current Ki and regeneration-related values should increase.", "Do not attack while charging."),
            Test("Gain Ki from attacks", "Land a short basic combo that generates Ki, then stop.", "Current Ki should increase while attack-state values may change briefly.", "Use the same combo on each repetition."),
            Test("Spend Stamina", "Perform one action that consumes stamina, such as a vanish or evasive.", "Current stamina and cost-related values should decrease.", "Do not guard or dash at the same time."),
            Test("Regenerate Stamina", "Remain neutral until stamina regenerates by a visible amount.", "Current stamina and regeneration values should increase.", "Do not move or spend stamina during recovery."),
            Test("Take Damage", "Allow the opponent to land one controlled hit, then compare.", "Current health should decrease; defense or hit-state fields may change.", "Use one repeatable attack and avoid guard."),
            Test("Heal / Recover Health", "Trigger one controlled healing or regeneration effect.", "Current health and recovery values should increase.", "Avoid simultaneous buffs when possible."),
            Test("KO / Revive", "Allow a controlled knockout or respawn transition, then compare after control returns.", "Health, life-state, revival, and object-state fields may reset.", "Capture a fresh baseline if the fighter object address changes.")),

        Category("Damage",
            Test("Deal Basic Attack Damage", "Land one short basic attack string and stop immediately.", "Opponent health decreases; basic-attack output/state values may change.", "Do not mix skills or Ki blasts."),
            Test("Use Basic Ki Blast", "Fire one basic Ki blast and wait for impact.", "Opponent health and basic Ki-blast state/output values may change.", "Use a single blast, not a volley."),
            Test("Use Strike Skill", "Use one strike skill and compare immediately after impact.", "Strike output, skill-state, cooldown, and resource values may change.", "Use the same skill for all repetitions."),
            Test("Use Ki Blast Skill", "Use one Ki-blast skill and compare immediately after impact.", "Ki-blast output, skill-state, cooldown, and Ki values may change.", "Use the same skill and charge level."),
            Test("Use Evasive Skill", "Trigger one evasive skill and compare after the animation begins or ends.", "Evasive-state, stamina cost, invulnerability, and cooldown fields may change.", "Avoid receiving a second hit.")),

        Category("Defense",
            Test("Guard", "Hold guard from a neutral state, then compare while guard is active.", "Guard-state, defense, and stamina-behavior fields may change.", "Do not receive an attack for the first pass."),
            Test("Perfect Guard", "Perform one perfect guard and compare immediately after the guard effect.", "Perfect-guard flags, timing windows, and resource fields may change.", "Use a repeatable opponent attack."),
            Test("Receive Stamina Break", "Allow one controlled stamina break and compare while broken.", "Stamina, recovery delay, broken-state, and timers should change.", "Do not transform during the test."),
            Test("Receive Debuff", "Apply one known debuff to the selected fighter and compare.", "Debuff flags, timers, multipliers, or resistances may change.", "Use only one debuff source."),
            Test("Apply Buff / Effect", "Activate one known buff or temporary effect and compare.", "Buff flags, timers, and multipliers may change.", "Avoid transformations unless that is the selected test."),
            Test("Remove Buff / Effect", "Let one known buff expire or remove it, then compare.", "The matching flag, timer, or multiplier should return toward baseline.", "Do not apply another effect during removal.")),

        Category("Transformation",
            Test("Transform", "Activate one transformation and compare after the transformation is fully active.", "Form state, multipliers, resources, skills, and appearance references may change.", "Remain still after transforming."),
            Test("De-transform", "Return from the active form to base and compare after control returns.", "Transformation fields should return or change in the opposite direction.", "Use the same form as the transform test."),
            Test("Skill Cooldown Start / End", "Use one skill with a visible cooldown, compare at start, then repeat at cooldown end.", "Cooldown timers and availability flags should change predictably.", "Keep all other actions neutral.")),

        Category("Movement",
            Test("Step / Dash", "Perform one short step or dash and compare during or immediately after it.", "Movement state, velocity, stamina cost, and timers may change.", "Use one direction consistently."),
            Test("Movement / Flight", "Move steadily in one direction or ascend in flight, then compare.", "Velocity, movement mode, position-related, and animation-state fields may change.", "Do not attack while moving."),
            Test("Z-Vanish", "Perform one Z-vanish and compare immediately after the teleport.", "Stamina, teleport state, target-relative, and cooldown fields may change.", "Avoid chaining into an attack."),
            Test("Lock-on Target Change", "Switch lock-on from one target to another and compare.", "Target pointers, target slot, and targeting state should change.", "Use a multi-target training setup.")),

        Category("Custom",
            Test("Custom Action", "Define one isolated action in the main app label box, capture a baseline, perform it once, and compare.", "Direction depends on the custom experiment.", "Change only one thing between baseline and comparison."))
    ];

    internal static ExperimentTestDefinition DefaultTest => Categories[0].Tests[0];

    internal static ExperimentTestDefinition? FindByName(string name) =>
        Categories.SelectMany(category => category.Tests)
            .FirstOrDefault(test => string.Equals(test.Name, name, StringComparison.OrdinalIgnoreCase));

    private static ExperimentCategoryDefinition Category(string name, params ExperimentTestDefinition[] tests) =>
        new(name, tests.Select(test => test with { Category = name }).ToArray());

    private static ExperimentTestDefinition Test(
        string name,
        string instruction,
        string expectedDirection,
        string isolationTip) =>
        new(string.Empty, name, instruction, expectedDirection, isolationTip);
}
