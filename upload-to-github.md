# GitHub Upload Guide

## Step-by-Step Instructions

### 1. Create GitHub Repository

1. Go to [GitHub.com](https://github.com) and sign in
2. Click the **"+"** icon in the top right corner
3. Select **"New repository"**
4. Fill in the details:
   - **Repository name**: `PlayNite-DualSense-Battery-Status`
   - **Description**: `DualSense Battery Plugin for Playnite - Shows PS5 controller battery status`
   - **Visibility**: Choose **Public** (so others can download)
   - **Don't** check "Add a README file" (we already have one)
   - **Don't** check "Add .gitignore"
   - **Don't** check "Choose a license"
5. Click **"Create repository"**

### 2. Upload Your Code

#### Option A: Using GitHub Desktop (Recommended for beginners)

1. Download and install [GitHub Desktop](https://desktop.github.com/)
2. Sign in with your GitHub account
3. Click **"Clone a repository from the Internet"**
4. Select your new repository and choose a local path
5. Click **"Clone"**
6. Copy all your project files into the cloned folder:
   - `Plugin/` folder
   - `Helper/` folder
   - `extension.yaml`
   - `README.md`
   - `.github/workflows/build.yml`
7. In GitHub Desktop, you'll see all the files listed
8. Add a commit message: `"Initial commit: DualSense Battery Plugin for Playnite"`
9. Click **"Commit to main"**
10. Click **"Push origin"**

#### Option B: Using Command Line

1. Open Command Prompt or PowerShell
2. Navigate to your project folder
3. Run these commands:

```bash
# Initialize git repository
git init

# Add all files
git add .

# Create initial commit
git commit -m "Initial commit: DualSense Battery Plugin for Playnite"

# Add your GitHub repository as remote
git remote add origin https://github.com/YOUR_USERNAME/PlayNite-DualSense-Battery-Status.git

# Push to GitHub
git push -u origin main
```

### 3. Verify the Build

1. Go to your repository on GitHub
2. Click the **"Actions"** tab
3. You should see a workflow running called "Build DualSense Battery Plugin"
4. Wait for it to complete (usually 2-3 minutes)
5. Click on the completed workflow
6. Scroll down to **"Artifacts"** section
7. Download the `DualSenseBattery` artifact (this is your `.pext` file)

### 4. Install in Playnite

1. Open Playnite
2. Go to **Add-ons** in the main menu
3. Click **"Install from file"**
4. Select the downloaded `.pext` file
5. Restart Playnite if prompted

### 5. Test the Plugin

1. Connect your DualSense controller
2. The plugin should automatically detect it
3. You should see the battery status in your theme (if you added the theme element)

## Troubleshooting

### Build Fails
- Check the **Actions** tab for error messages
- Make sure all files are properly uploaded
- Verify the `.github/workflows/build.yml` file exists

### Plugin Doesn't Work
- Make sure you have a DualSense controller connected
- Check Playnite's add-ons list to see if the plugin is installed
- Try restarting Playnite

### Theme Integration Issues
- Make sure you added the correct XAML code to your theme
- Check that the theme element is visible and positioned correctly
- Verify the plugin is enabled in Playnite

## File Structure

Your repository should look like this:

```
PlayNite-DualSense-Battery-Status/
├── .github/
│   └── workflows/
│       └── build.yml
├── Helper/
│   ├── Program.cs
│   ├── DualSenseBatteryHelper.csproj
│   └── ...
├── Plugin/
│   ├── Plugin.cs
│   ├── Views/
│   │   ├── AutoSystemBatteryReplacementControl.xaml
│   │   ├── AutoSystemBatteryReplacementControl.xaml.cs
│   │   ├── SystemBatteryReplacementControl.xaml
│   │   ├── SystemBatteryReplacementControl.xaml.cs
│   │   ├── BatteryBarControl.xaml
│   │   └── BatteryBarControl.xaml.cs
│   ├── DualSenseBatteryPlugin.csproj
│   └── ...
├── extension.yaml
├── README.md
└── upload-to-github.md
```

## Next Steps

Once your repository is set up:

1. **Share the repository** with the Playnite community
2. **Update the README** with your specific repository URL
3. **Create releases** for major updates
4. **Respond to issues** and pull requests from users

## Support

If you need help:
- Check the [Playnite Discord](https://discord.gg/playnite)
- Create an issue in your GitHub repository
- Check the [Playnite documentation](https://playnite.link/docs/)
