using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Collections;

namespace wix
{
    class Program
    {
        /// <summary>
        /// The operation completed successfully.
        /// </summary>
        public const int ERROR_SUCCESS = 0;
        /// <summary>
        /// Incorrect function.
        /// </summary>
        public const int ERROR_INVALID_FUNCTION = 1;
        /// <summary>
        /// The system cannot find the file specified.
        /// </summary>
        public const int ERROR_FILE_NOT_FOUND = 2;
        /// <summary>
        /// The system cannot find the path specified.
        /// </summary>
        public const int ERROR_PATH_NOT_FOUND = 3;
        /// <summary>
        /// The system cannot open the file.
        /// </summary>
        public const int ERROR_TOO_MANY_OPEN_FILES = 4;
        /// <summary>
        /// Access is denied.
        /// </summary>
        public const int ERROR_ACCESS_DENIED = 5;

        const Int32 DRIVER_PACKAGE_REPAIR = 0x00000001;
        const Int32 DRIVER_PACKAGE_SILENT = 0x00000002;
        const Int32 DRIVER_PACKAGE_FORCE = 0x00000004;
        const Int32 DRIVER_PACKAGE_ONLY_IF_DEVICE_PRESENT = 0x00000008;
        const Int32 DRIVER_PACKAGE_LEGACY_MODE = 0x00000010;
        const Int32 DRIVER_PACKAGE_DELETE_FILES = 0x00000020;

        [DllImport("DIFXApi.dll", CharSet = CharSet.Unicode)]
        public static extern Int32 DriverPackagePreinstall(string DriverPackageInfPath, Int32 Flags);

        static int no = 0;

        static StreamWriter sw;

        static List<string> components = new List<string>();

        static Hashtable dircache = new Hashtable();

        static string mainexeid = "";

        static string basedir = "";

        /// <summary>Publisher shown in Programs and Features / the MSI summary</summary>
        public static string manufacturer = "Nevermind";

        /// <summary>Product name, install folder name and start menu folder name</summary>
        public static string productName = "Mission Planner";

        /// <summary>
        /// UpgradeCode identifies the product line for upgrades. This is deliberately different from the
        /// upstream Mission Planner installer so this build does not silently replace an official install.
        /// Pass --upgradecode={625389D7-EB3C-4d77-A5F6-A285CF99437D} to upgrade over the upstream installer.
        /// </summary>
        public static string upgradeCode = "{3F1B7C52-8D0A-4E6B-9B27-6A5E2C9D4F10}";

        /// <summary>
        /// Run DPInst driver install and add the driver signing certificate to the trusted root store.
        /// Off by default: Windows 10/11 use the in-box USB serial driver for ArduPilot boards.
        /// </summary>
        public static bool includeDrivers = false;

        /// <summary>Registry safe version of the manufacturer name</summary>
        public static string manufacturerKey
        {
            get { return System.Text.RegularExpressions.Regex.Replace(manufacturer, "[^A-Za-z0-9]", ""); }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // usage: wix.exe <bin directory> [output name] [--manufacturer=NAME] [--product=NAME] [--upgradecode={GUID}] [--drivers]
            var positional = new List<string>();
            foreach (var arg in args)
            {
                if (arg.StartsWith("--manufacturer=", StringComparison.OrdinalIgnoreCase))
                    manufacturer = arg.Substring("--manufacturer=".Length).Trim('"');
                else if (arg.StartsWith("--product=", StringComparison.OrdinalIgnoreCase))
                    productName = arg.Substring("--product=".Length).Trim('"');
                else if (arg.StartsWith("--upgradecode=", StringComparison.OrdinalIgnoreCase))
                    upgradeCode = arg.Substring("--upgradecode=".Length).Trim('"');
                else if (arg.Equals("--drivers", StringComparison.OrdinalIgnoreCase))
                    includeDrivers = true;
                else
                    positional.Add(arg);
            }

            if (positional.Count == 0)
            {
                Console.WriteLine("Bad Directory");
                Console.WriteLine("usage: wix.exe <bin directory> [output name] [--manufacturer=NAME] [--product=NAME] [--upgradecode={GUID}] [--drivers]");
                return;
            }

            Drivers.process();

            string path = positional[0];
            basedir = path;
            //Path.GetDirectoryName(Application.ExecutablePath) + Path.DirectorySeparatorChar+ 
            string file = "installer.wxs";

            string outputfilename = "MissionPlanner";

            if (positional.Count > 1)
                outputfilename = positional[1];

            Console.WriteLine("Manufacturer: " + manufacturer + ", Product: " + productName + ", UpgradeCode: " + upgradeCode + ", Drivers: " + includeDrivers);

            string exepath = Path.GetFullPath(path) + Path.DirectorySeparatorChar + "MissionPlanner.exe";
            string version = Assembly.LoadFile(exepath).GetName().Version.ToString();

            System.Diagnostics.FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(exepath);

            sw = new StreamWriter(file);

            header(fvi.ProductVersion);

            sw.WriteLine("    <Directory Id=\"INSTALLDIR\" Name=\"" + productName + "\">");

            sw.WriteLine(@"        <Component Id=""InstallDirPermissions"" Guid=""{525389D7-EB3C-4d77-A5F6-A285CF99437D}"" KeyPath=""yes""> 
            <CreateFolder> 
                <Permission User=""Everyone"" GenericAll=""yes"" /> 
            </CreateFolder>
        </Component>");

            dodirectory(path, 0);


            footer(path);

            sw.Close();

    

            string fn = outputfilename + "-" + fvi.ProductVersion;

            StreamWriter st = new StreamWriter("create.bat", false);

            st.WriteLine("del installer.wixobj");

            st.WriteLine(@"""%wix%\bin\candle"" installer.wxs -ext WiXNetFxExtension -ext WixDifxAppExtension -ext WixUIExtension.dll -ext WixUtilExtension -ext WixIisExtension");

            st.WriteLine(@"""%wix%\bin\light"" installer.wixobj ""%wix%\bin\difxapp_x86.wixlib"" -sval -o " + fn + ".msi -ext WiXNetFxExtension -ext WixDifxAppExtension -ext WixUIExtension.dll -ext WixUtilExtension -ext WixIisExtension");

            st.WriteLine("pause");

            st.WriteLine(@"""C:\Program Files\7-Zip\7z.exe"" a -tzip -xr!beta.bat -xr!cameras.xml -xr!firmware.hex -xr!*.zip -xr!stats.xml -xr!*.bin -xr!*.xyz -xr!*.sqlite -xr!*.dxf -xr!*.zip -xr!*.h -xr!*.param -xr!ParameterMetaData.xml -xr!translation -xr!mavelous_web -xr!stats.xml -xr!driver -xr!*.etag -xr!srtm -xr!*.rlog -xr!*.zip -xr!*.tlog -xr!config.xml -xr!gmapcache -xr!eeprom.bin -xr!dataflash.bin -xr!*.new -xr!*.log -xr!ArdupilotPlanner.log* -xr!cameras.xml -xr!firmware.hex -xr!*.zip -xr!stats.xml -xr!ParameterMetaData.xml -xr!*.etag -xr!*.rlog -xr!*.tlog -xr!config.xml -xr!gmapcache -xr!eeprom.bin -xr!dataflash.bin -xr!*.new " + fn + @".zip " + path + "*");

            st.Close();

            //runProgram("create.bat");


        }

        static void runProgram(string run)
        {
            System.Diagnostics.Process P = new System.Diagnostics.Process();
            P.StartInfo.FileName = run;

            //            P.StartInfo.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
            P.StartInfo.UseShellExecute = true;
            P.Start();
        }

        static void header(string version)
        {
            string newid = System.Guid.NewGuid().ToString();

            newid = "*";

            string data = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Wix xmlns=""http://schemas.microsoft.com/wix/2006/wi"" xmlns:netfx=""http://schemas.microsoft.com/wix/NetFxExtension"" xmlns:difx=""http://schemas.microsoft.com/wix/DifxAppExtension"" xmlns:iis='http://schemas.microsoft.com/wix/IIsExtension' >


    <Product Id=""" + newid + @""" Name=""" + productName + @""" Language=""1033"" Version=""" + version + @""" Manufacturer=""" + manufacturer + @""" UpgradeCode=""" + upgradeCode + @""">

    <Package Description=""" + productName + @" Installer"" Comments=""" + productName + @" Installer"" Manufacturer=""" + manufacturer + @""" InstallerVersion=""200"" Compressed=""yes"" />

    <Upgrade Id=""" + upgradeCode + @""">
        <UpgradeVersion OnlyDetect=""yes"" Minimum=""" + version + @""" Property=""NEWERVERSIONDETECTED"" IncludeMinimum=""no"" />
        <UpgradeVersion OnlyDetect=""no"" Minimum=""0.0.0"" Maximum=""" + version + @""" Property=""OLDERVERSIONBEINGUPGRADED"" IncludeMinimum=""yes"" IncludeMaximum=""yes"" />
    </Upgrade>

    <InstallExecuteSequence>
        <RemoveExistingProducts After=""InstallInitialize"" />
    </InstallExecuteSequence>

    <SetProperty Action='SetTARGETDIR' Before='LaunchConditions' Id='TARGETDIR' Value=""[ProgramFilesFolder]"" />

    <PropertyRef Id=""WIX_IS_NETFRAMEWORK_46_OR_LATER_INSTALLED"" />

    <Condition Message=""This application requires .NET Framework 4.6.1. Please install the .NET Framework then run this installer again.""><![CDATA[Installed OR WIX_IS_NETFRAMEWORK_46_OR_LATER_INSTALLED]]></Condition>

    <Media Id=""1"" Cabinet=""product.cab"" EmbedCab=""yes"" />

    <Directory Id=""TARGETDIR"" Name=""SourceDir"">
                ";

            sw.WriteLine(data);
        }

        static void footer(string path)
        {

            string data = @"
        </Directory>

        <Directory Id=""ProgramMenuFolder"">
            <Directory Id=""ApplicationProgramsFolder"" Name=""" + productName + @""" />
        </Directory>
    </Directory>

" + (includeDrivers ? @"
    <Binary Id=""signedcer""  SourceFile=""..\Drivers\signed.cer"" />
  
    <CustomAction  Id='Drivercleanup' Execute='deferred' 
    Directory='Drivers'  ExeCommand='[Drivers]DriverCleanup.exe' Return='ignore' Impersonate='no'/>

    <CustomAction  Id='Install_signed_Driver86' Execute='deferred' 
    Directory='Drivers'  ExeCommand='[Drivers]DPInstx86.exe' Return='ignore' Impersonate='no'/>
    <CustomAction  Id='Install_signed_Driver64' Execute='deferred' 
    Directory='Drivers'  ExeCommand='[Drivers]DPInstx64.exe' Return='ignore' Impersonate='no'/>

    <InstallExecuteSequence>  
    <Custom Action=""Install_signed_Driver86""  After=""CreateShortcuts"">NOT 
    Installed AND NOT VersionNT64</Custom>
    <Custom Action=""Install_signed_Driver64""  After=""CreateShortcuts"">NOT 
    Installed AND VersionNT64</Custom>
    <Custom Action=""Drivercleanup""  After=""CreateShortcuts"">NOT Installed</Custom>
    </InstallExecuteSequence>
" : "") + @"

    <InstallExecuteSequence>
    <Custom Action='comReg' After='CreateShortcuts'>NOT REMOVE</Custom>
    <Custom Action='comUnreg' Before='RemoveFiles'>Installed</Custom> 
    <Custom Action='comReg64' After='CreateShortcuts'>NOT REMOVE AND VersionNT64</Custom>
    <Custom Action='comUnreg64' Before='RemoveFiles'>Installed AND VersionNT64</Custom> 
    </InstallExecuteSequence>
 
    <CustomAction
    Id='comReg' Impersonate='no' Execute='deferred' 
    Directory='INSTALLDIR'
    ExeCommand='[SystemFolder]cmd.exe /c """"[WindowsFolder]Microsoft.NET\Framework\v4.0.30319\regasm.exe"" ""[INSTALLDIR]tlogThumbnailHandler.dll"" /codebase""'
    Return='ignore' />
 
    <CustomAction
    Id='comUnreg' Impersonate='no' Execute='deferred' 
    Directory='INSTALLDIR'
    ExeCommand='[SystemFolder]cmd.exe /c """"[WindowsFolder]Microsoft.NET\Framework\v4.0.30319\regasm.exe"" /u ""[INSTALLDIR]tlogThumbnailHandler.dll""""'
    Return='ignore' />        

    <CustomAction
    Id='comReg64' Impersonate='no' Execute='deferred' 
    Directory='INSTALLDIR'
    ExeCommand='[SystemFolder]cmd.exe /c """"[WindowsFolder]Microsoft.NET\Framework64\v4.0.30319\regasm.exe"" ""[INSTALLDIR]tlogThumbnailHandler.dll"" /codebase""'
    Return='ignore' />
 
    <CustomAction
    Id='comUnreg64' Impersonate='no' Execute='deferred' 
    Directory='INSTALLDIR'
    ExeCommand='[SystemFolder]cmd.exe /c """"[WindowsFolder]Microsoft.NET\Framework64\v4.0.30319\regasm.exe"" /u ""[INSTALLDIR]tlogThumbnailHandler.dll""""'
    Return='ignore' />           

    <DirectoryRef Id=""ApplicationProgramsFolder"">
        <Component Id=""ApplicationShortcut"" Guid=""*"">
            <Shortcut Id=""ApplicationStartMenuShortcut10"" Name=""" + productName + @""" Description=""" + productName + @""" Target=""[INSTALLDIR]MissionPlanner.exe"" WorkingDirectory=""INSTALLDIR"" />
            <Shortcut Id=""UninstallProduct"" Name=""Uninstall " + productName + @""" Description=""Uninstalls " + productName + @""" Target=""[System64Folder]msiexec.exe"" Arguments=""/x [ProductCode]"" />
            <RegistryValue Root=""HKCU"" Key=""Software\" + manufacturerKey + @"\MissionPlanner"" Name=""installed"" Type=""integer"" Value=""1"" KeyPath=""yes"" />

            <RemoveFolder Id=""dltApplicationProgramsFolder"" Directory=""ApplicationProgramsFolder"" On=""uninstall"" />
" + (includeDrivers ? @"
            <iis:Certificate Id=""rootcert"" StoreLocation=""localMachine"" StoreName=""root"" Overwrite='yes' BinaryKey='signedcer' Request=""no"" Name='Michael Oborne' />
" : "") + @"
        </Component>
    </DirectoryRef>


    <Feature Id=""Complete"" Title=""" + productName + @""" Level=""1"">
        <ComponentRef Id=""InstallDirPermissions"" />
";
            sw.WriteLine(data);

            foreach (string comp in components)
            {
                sw.WriteLine(@"<ComponentRef Id=""" + comp + @""" />");
            }

            data = @"
            
        <ComponentRef Id=""ApplicationShortcut"" />
    </Feature>
        
    <!-- Step 2: Add UI to your installer / Step 4: Trigger the custom action -->
    <Property Id=""WIXUI_INSTALLDIR"" Value=""INSTALLDIR"" />

    <WixVariable Id=""WixUILicenseRtf"" Value=""licence.rtf"" />

    <UI>
        <UIRef Id=""WixUI_InstallDir"" />
        <Publish Dialog=""ExitDialog"" 
            Control=""Finish"" 
            Event=""DoAction"" 
            Value=""LaunchApplication"">WIXUI_EXITDIALOGOPTIONALCHECKBOX = 1 and NOT Installed</Publish>
    </UI>
    <Property Id=""WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT"" Value=""Launch " + productName + @""" />

    <!-- Step 3: Include the custom action -->
    <Property Id=""WixShellExecTarget"" Value=""[#" + mainexeid + @"]"" />
    <CustomAction Id=""LaunchApplication"" 
        BinaryKey=""WixCA"" 
        DllEntry=""WixShellExec""
        Impersonate=""yes"" />
    </Product>
    
</Wix>";

            sw.WriteLine(data);
        }

        static void dodirectory(string path, int level = 1)
        {
            string[] dirs = Directory.GetDirectories(path);

            string tabs = "".PadLeft(level + 1, '\t');
            string tabs2 = "".PadLeft(level + 2, '\t');
            string tabs3 = "".PadLeft(level + 3, '\t');

            if (level != 0)
            {
                if (dircache.ContainsKey(Path.GetFileName(path).Replace('-', '_')))
                {
                    sw.WriteLine(tabs+"<Directory Id=\"" + Path.GetFileName(path).Replace('-', '_') + no + "\" Name=\"" + Path.GetFileName(path) + "\">");
                }
                else
                {
                    sw.WriteLine(tabs + "<Directory Id=\"" + Path.GetFileName(path).Replace('-', '_') + "\" Name=\"" + Path.GetFileName(path) + "\">");
                }

                dircache[Path.GetFileName(path).Replace('-', '_')] = "";
            }

            string[] files = Directory.GetFiles(path);

            no++;

            string compname = fixname(Path.GetFileName(path));
            sw.WriteLine(tabs2 + "<Component Id=\"" + compname + "\" Guid=\"" + System.Guid.NewGuid().ToString() + "\">");
            components.Add(compname);

            foreach (string filepath in files)
            {
                if (filepath.ToLower().EndsWith("release\\config.xml") || filepath.ToLower().StartsWith("joystick") ||
                    filepath.ToLower().StartsWith("camera.xml") || filepath.ToLower().StartsWith("firmware.hex") || filepath.ToLower().EndsWith(".param") ||
                    filepath.ToLower().EndsWith(".bin") || filepath.ToLower().EndsWith(".etag") || filepath.ToLower().EndsWith("parametermetadata.xml") ||
                    filepath.ToLower().EndsWith(".zip") || filepath.ToLower().EndsWith(".rlog") || filepath.ToLower().Contains("stats.xml") || filepath.ToLower().Contains("beta.bat"))
                    continue;

                no++;


                if (filepath.EndsWith("MissionPlanner.exe"))
                {
                    mainexeid = "_" + no;

                    sw.WriteLine(tabs3 + "<File Id=\"" + mainexeid + "\" Source=\"" + filepath + "\" ><netfx:NativeImage Id=\"ngen_MissionPlannerexe\"/> </File>");

                    sw.WriteLine(@"<ProgId Id='MissionPlanner.tlog' Description='Telemetry Log'>
  <Extension Id='tlog' ContentType='application/tlog'>
     <Verb Id='open' Command='Open' TargetFile='" + mainexeid + @"' Argument='""%1""' />
  </Extension>
</ProgId>
<ProgId Id='MissionPlanner.dfbin' Description='Binary Log'>
  <Extension Id='dfbin' ContentType='application/dflog'>
     <Verb Id='open' Command='Open' TargetFile='" + mainexeid + @"' Argument='""%1""' />
  </Extension>
</ProgId>
<ProgId Id='MissionPlanner.log' Description='DF Log'>
  <Extension Id='log' ContentType='application/dflog'>
     <Verb Id='open' Command='Open' TargetFile='" + mainexeid + @"' Argument='""%1""' />
  </Extension>
</ProgId>
 <RegistryValue Root=""HKCR"" Key=""MissionPlanner.tlog\shellex\{BB2E617C-0920-11D1-9A0B-00C04FC2D6C1}"" Value=""{f3b857f1-0b79-4e77-9d0b-8b8b7e874f56}"" Type=""string"" Action=""write"" />
 <RegistryValue Root=""HKCR"" Key=""MissionPlanner.tlog\shellex\{e357fccd-a995-4576-b01f-234630154e96}"" Value=""{f3b857f1-0b79-4e77-9d0b-8b8b7e874f56}"" Type=""string"" Action=""write"" />

 <RegistryValue Root=""HKCR"" Key=""MissionPlanner.dfbin\shellex\{BB2E617C-0920-11D1-9A0B-00C04FC2D6C1}"" Value=""{f3b857f1-0b79-4e77-9d0b-8b8b7e874f56}"" Type=""string"" Action=""write"" />
 <RegistryValue Root=""HKCR"" Key=""MissionPlanner.dfbin\shellex\{e357fccd-a995-4576-b01f-234630154e96}"" Value=""{f3b857f1-0b79-4e77-9d0b-8b8b7e874f56}"" Type=""string"" Action=""write"" />

 <RegistryValue Root=""HKCR"" Key=""MissionPlanner.log\shellex\{BB2E617C-0920-11D1-9A0B-00C04FC2D6C1}"" Value=""{f3b857f1-0b79-4e77-9d0b-8b8b7e874f56}"" Type=""string"" Action=""write"" />
 <RegistryValue Root=""HKCR"" Key=""MissionPlanner.log\shellex\{e357fccd-a995-4576-b01f-234630154e96}"" Value=""{f3b857f1-0b79-4e77-9d0b-8b8b7e874f56}"" Type=""string"" Action=""write"" />
");

                }
                else
                {
                    sw.WriteLine(tabs3 + "<File Id=\"" + fixname(Path.GetFileName(filepath)) + "\" Source=\"" + filepath + "\" />");
                }
            }

            sw.WriteLine(tabs2 + "</Component>");

            foreach (string dir in dirs)
            {
                if (dir.ToLower().EndsWith("gmapcache") || dir.ToLower().EndsWith("srtm") || dir.ToLower().EndsWith("logs"))
                    continue;
                dodirectory(dir, level + 1);
            }

            if (level != 0)
                sw.WriteLine(tabs + "</Directory>");
        }

        static bool IsNumeric(string s)
        {
            float output;
            return float.TryParse(s, out output);
        }

        static Hashtable usedfns = new Hashtable();

        static string fixname(string name)
        {
            if (name == "")
                return name + "_1";

            name = name.Replace("-", "_");
            name = name.Replace(" ", "_");
            name = name.Replace(" ", "_");
            name = name.Replace(".", "_");

            if (IsNumeric(name[0].ToString()))
                name = "_" + name;

            string nameorig = name;

            int a = 1;
            while (usedfns.ContainsKey(name.ToLower()))
            {
                name = nameorig + "_" + a;
                a++;
            }

            usedfns[name.ToLower()] = 1;

            return name;
        }
    }
}