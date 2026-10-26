MuAutoReset - project package

Files:
- MainForm.cs: MainForm with separate Devil X/Y settings and Devil automation flow.
- Program.cs: WinForms entry point.
- MuAutoReset.csproj: .NET 8 Windows Forms project file.
- MemoryWriter.cs: compatible process-memory helper included because the original separate file was not present in the files available for this package.

Open MuAutoReset.csproj in Visual Studio 2022 with the .NET desktop development workload installed.
Build target: Windows / .NET 8.

IMPORTANT:
This package has not been compiled or tested against the user's MU client. The included MemoryWriter.cs is a compatible implementation, not a copy of the user's original MemoryWriter file. Back up your original project before replacing anything.

BUILD EXE AUTOMATICALLY (without installing .NET on your PC):
1. Upload this project folder to a new GitHub repository (include the hidden .github folder).
2. Open the repository's Actions tab and select "Build Windows EXE".
3. Click "Run workflow" and wait for the run to finish successfully.
4. Open the completed run and download the "MuAutoReset-win-x64" artifact. Extract it to get the executable and any supporting files.

Note: This workflow is included as a build path; it has not been executed from this environment, so a successful build is not yet confirmed.
