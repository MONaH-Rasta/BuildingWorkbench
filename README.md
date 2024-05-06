# BuildingWorkbench

Oxide plugin for Rust. Extends the range of the workbench to work inside the entire building.

## About

For players with the permission the range of workbench will be extended throughout the entire building.

* Note: Building needs to have a tool cupboard in order to work.

## Permission

* `buildingworkbench.use` - Allows players to have their workbench extended throughout the entire building.
  * After adding the permission you need to reload the plugin or have the players rejoin.
* `buildingworkbench.cancelcraft` - Players with this permission will have their crafting canceled if their workbench level changes below the crafting level. All items will be returned.

### Grant Example

 `o.grant group default buildingworkbench.use` - will grant all players extended workbench.

## Configuration

```json
{
  "Display workbench built notification": true,
  "Display cancel craft notification": true,
  "Inside building check frequency (Seconds)": 3.0,
  "Distance from base to be considered inside building (Meters)": 16.0,
  "Required distance from last update (Meters)": 5.0
}
```

## Localization

```json
{
  "Chat": "<color=#bebebe>[<color=#de8732>Building Workbench</color>] {0}</color>",
  "Notification": "Your workbench range has been increased to work inside your building",
  "CraftCanceledV1": "Your workbench level has changed. Crafts that required a higher level have been cancelled."
}
```
