# Bulk Qi Coin Purchases 1.0.0

A Stardew Valley 1.6 SMAPI mod that replaces the Casino cashier's repeated 1,000g confirmation with a quantity prompt.

## Usage

Interact with the Casino cashier as usual, enter the number of Qi Coins you want, and confirm the purchase. The selector:

- defaults to 1,000 Qi Coins;
- shows the total purchase price;
- keeps the vanilla exchange rate of 10g per Qi Coin;
- caps the quantity at what you can currently afford.

The mod only changes the Casino cashier interaction. Qi Coins earned from games and spent in the Casino shop are unaffected.

## Installation

1. Install Stardew Valley 1.6 and SMAPI 4.0 or later.
2. Copy the `BulkQiCoinPurchases` mod folder into your Stardew Valley `Mods` folder.

## Building

Run `build.ps1`, or run:

```powershell
dotnet build .\BulkQiCoinPurchases.csproj -c Release
```

`Pathoschild.Stardew.ModBuildConfig` locates the Stardew Valley and SMAPI assemblies and creates the release package under `bin`.