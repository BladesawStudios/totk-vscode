# TKVSC Setup and Usage Guide

## Prerequisites
- Already have installed [VS Code](https://code.visualstudio.com/download)
- Installed [Python 3.12](https://www.python.org/downloads/release/python-31210/)
- Already have a [RomFS Dump](https://gamebanana.com/tuts/19858) of TOTK


## Setup Steps
1. Launch VS Code
2. Per the labels in the below screenshot:
    1. Select the Extensions tab
    2. Select the hamburger icon (the three lines)
    3. Select `Install from VSIX...`
    
<img src="https://raw.githubusercontent.com/TKVSC-Team/totk-vscode/refs/heads/main/docs/tutorials/tkvsc_setup/setup_installsteps.png" alt="A screenshot of the VS Code Extensions tab with numbered icons at the positions referenced above" w="90%"/>

3. Locate and select the `totk-vscode-<version>.vsix`, where `<version>` will vary depending on the version of the extension you downloaded
4. After the plugin installs, you should see A prompt in the bottom-right corner of the window asking you to select your TOTK RomFS Dump path. Select the folder that *contains* the folders shown below:

<img src="https://raw.githubusercontent.com/TKVSC-Team/totk-vscode/refs/heads/main/docs/tutorials/tkvsc_setup/setup_selectromfs.png" alt="A screenshot of the prompt directing the user to select the folder containing their RomFS Dump" w="90%"/>
<img src="https://raw.githubusercontent.com/TKVSC-Team/totk-vscode/refs/heads/main/docs/tutorials/tkvsc_setup/setup_romfsexample.png" alt="A screenshot of a File Explorer window showing many folders in alphabetical order that appear in a valid TOTK RomFS Dump folder" w="90%"/>

5. Next, you will be prompted to select a folder for new Project Folders to be created in. While you can import existing folders manually, you will also be able to create new Project root folders from within TKVSC. For organizational purposes, it is recommended to choose a folder that will only be used for TOTK Mod Projects:

<img src="https://raw.githubusercontent.com/TKVSC-Team/totk-vscode/refs/heads/main/docs/tutorials/tkvsc_setup/setup_selectromfs.png" alt="A screenshot of the prompt directing the user to select the a directory for new Mod Projects created within TKVSC to be stored" w="90%"/>

6. Lastly, you will be asked if you'd like to import existing Mod Projects from TKMM. This will cause all folders located in the `Recent Projects` list within TKMM to appear in the `Your Projects` tab of TKVSC. It's your choice, and you can always manually import these folders later if you choose not to at the moment. If you haven't used TKMM before, select no

<img src="https://raw.githubusercontent.com/TKVSC-Team/totk-vscode/refs/heads/main/docs/tutorials/tkvsc_setup/setup_importfromtkmm.png" alt="A screenshot of the prompt asking the user if they'd like to import existing Mod Project folders from TKMM" w="90%"/>


You've now completed setup for TKVSC. At this point, the tool will take a brief moment to build internal databases based on your RomFS Dump and any Project folders you've imported.

Explore on your own, or continue reading for information about the two new tabs TKVSC adds to the Activity Bar:

