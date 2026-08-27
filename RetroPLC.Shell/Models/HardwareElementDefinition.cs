// SPDX-License-Identifier: GPL-3.0-or-later
namespace RetroPLC.Shell.Models;

/// <summary>Description of a new CONFIGURATION element to add to a project.</summary>
public sealed record NewConfigurationDefinition(string Name);

/// <summary>Description of a new RESOURCE element to add to a CONFIGURATION.</summary>
public sealed record NewResourceDefinition(string Name, string Processor = "PLC");

/// <summary>Description of a new TASK element to add to a RESOURCE.</summary>
public sealed record NewTaskDefinition(
    string Name,
    string Trigger,
    string Interval,
    string EventExpression,
    int Priority);
