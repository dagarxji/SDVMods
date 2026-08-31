# Release checklist

This project needs a real Stardew/SMAPI development machine for the final compile and runtime smoke test.

- [ ] `dotnet restore`
- [ ] `dotnet build -c Release`
- [ ] Confirm SMAPI loads the manifest and DLL with no warnings/errors.
- [ ] Create a new farm and test the **Season Exchange Settings** button on character/farm creation.
- [ ] Save custom creation settings, start the farm, and confirm the values carry into the save.
- [ ] Confirm the Spring 1 fallback setup appears if creation-screen setup was not used.
- [ ] Test vanilla normal bundles.
- [ ] Test vanilla remixed bundles.
- [ ] Test a choose-N bundle and confirm unused ingredient flags resolve normally when the bundle completes.
- [ ] Test normal/silver/gold/iridium candidate pricing.
- [ ] Test a requirement one, two, and three seasons away.
- [ ] Verify the button appears only while physically at the Community Center and never in the remote bundle viewer.
- [ ] Complete a bundle and collect its normal reward.
- [ ] Complete the final bundle in an area and verify the normal restoration sequence.
- [ ] Test with Stardew Valley Expanded if installed.
- [ ] Test with Ridgeside Village if installed.
- [ ] Test with East Scarp if installed.
- [ ] Test with at least one custom crop/content pack.
- [ ] Test co-op host/farmhand settings sync and an exchange performed by a farmhand.
- [ ] Review the SMAPI log for catalog warnings and add `compatibility.json` overrides only where necessary.
