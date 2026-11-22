# Submitting Nikomi to the Microsoft Store

From the Store build on feature/store to Nikomi in the Store. Written on 22 November 2026 from
Microsoft's Partner Center documentation for MSIX apps; Partner Center's pages may have moved since.
The text to paste is in [store-listing.md](store-listing.md); how the package is built is in the
manual, "Microsoft Store build".

## 1. Before the first submission

1. **Test a start at sign-in.** With the test package installed and Start with Windows on, sign out and
   back in: the packaged Nikomi should start in the tray with no window. Its log
   (`Nikomi.log` in the folder Settings names) should have no "Could not tell how Windows started
   Nikomi" line.
2. **Merge feature/store into main** once that works, so the privacy policy, the package and the
   listing are on main.

## 2. Reserve the name

1. In Partner Center, open **Apps and games** (https://aka.ms/submitwindowsapp), choose
   **New product**, then **MSIX or PWA app**.
2. Enter **Nikomi**, choose **Check availability**, then **Reserve product name**.

A reserved name that is not used in a submission within three months is released again.

## 3. Put the product identity in the package

1. In the app's overview, under **Product management**, open **Product identity**. It shows
   `Package/Identity/Name`, `Package/Identity/Publisher` (starting with `CN=`) and
   `Package/Properties/PublisherDisplayName`.
2. Put them in `src\Nikomi\Package\Package.appxmanifest`: `Identity` `Name` and `Publisher`, and
   `PublisherDisplayName` under `Properties`. The values are case-sensitive; copy them exactly, spaces
   and punctuation included. Done on 22 November 2026: `IndieSoftware.Nikomi`,
   `CN=DA748DD7-9206-47F7-83C2-202E549BC85C`, `Indie Software`.
3. Check the version: `Identity` `Version` is `Directory.Build.props`' version with a fourth part of 0
   (Nikomi.Check fails otherwise). The Store needs the fourth part to stay 0. Give each update a higher
   version, so installed copies update.
4. Build the package and run the checks:

   ```powershell
   dotnet build src\Nikomi\Nikomi.csproj -c Release -p:Store=true
   ```

   ```powershell
   dotnet run --project tests\Nikomi.Check -c Release
   ```

   The file to upload is `publish\store\Nikomi_<version>_x64_Test\Nikomi_<version>_x64.msix`. It is
   unsigned, which is fine: the Store signs it.
5. Optional: run the Windows App Certification Kit on it (it comes with the Windows SDK), which runs
   the same technical tests as the Store's certification.

Remove any test copy before installing Nikomi from the Store: the old one with a placeholder identity,
and one registered from the build folder, which has the Store's:
`Get-AppxPackage Nikomi.LocalTest | Remove-AppxPackage`, then the same with `IndieSoftware.Nikomi`.

## 4. Fill in the submission

In the app's overview, under **Product release**, choose **Start submission**. Then each section:

- **Pricing and availability:** Markets (all, the default), Audience public, Discoverability "available
  and discoverable", Schedule "as soon as possible", **Base price: Free**.
- **Properties:** Category **Utilities & tools**. Privacy policy URL, website and support contact
  from the table in store-listing.md, "Store listing details". Nikomi collects no personal data, but a
  packaged Win32 app always needs a privacy policy URL.
- **Age ratings:** answer the questionnaire (store-listing.md, "Age rating") and choose
  **Save and generate**. The rating carries over to later updates.
- **Packages:** upload the `.msix` from step 3. Keep the device family option checked. The section reads
  "Incomplete" until everything required is set, even while the package already shows as validated. A
  package that fails validation has to be removed, fixed and uploaded again.
- **Store listings:** English (United States). Paste the description, short description, features and
  search terms, and upload the four screenshots from `docs\store\` with their captions. Leave
  "What's new in this version" empty the first time.
- **Submission options:** under **Restricted capabilities**, paste the runFullTrust answer from
  store-listing.md. Leave the publishing hold at the default (publish as soon as it passes) unless you
  want to choose the day. Notes for certification can stay empty: Nikomi has no sign-in or hidden part.

## 5. Submit

Choose **Submit for certification**. Certification can take up to three business days: preprocessing,
then security, technical and content tests, then publishing.

- **If it fails,** the certification report says which test or policy. Fix it and start a new
  submission.
- **If it passes,** the Store signs the package and publishes it. From then on the release can no
  longer be cancelled. The listing usually shows up within about 15 minutes, and the status turns to
  "In the Store".

## Later updates

Raise `Version` in `Directory.Build.props` and the manifest's `Identity` `Version` (with a fourth part
of 0), build the package as in step 3, start a new submission, upload the new `.msix`, and fill in
"What's new in this version".
