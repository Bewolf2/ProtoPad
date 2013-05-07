using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using ActiproSoftware.Text;
using ActiproSoftware.Text.Implementation;
using ActiproSoftware.Text.Languages.CSharp.Implementation;
using ActiproSoftware.Text.Languages.DotNet;
using ActiproSoftware.Text.Languages.DotNet.Ast.Implementation;
using ActiproSoftware.Text.Languages.DotNet.Reflection;
using ActiproSoftware.Text.Parsing;
using ActiproSoftware.Text.Parsing.Implementation;
using ActiproSoftware.Windows.Controls.SyntaxEditor.IntelliPrompt.Implementation;
using Microsoft.CSharp;
using ServiceDiscovery;
using mshtml;

using StumDE.Misc;

namespace ProtoPad_Client
{
    public partial class MainWindow : INotifyPropertyChanged {
        private IHTMLElement _htmlHolder;
        private IHTMLWindow2 _htmlWindow;
        private string _currentWrapText;
        private IProjectAssembly _projectAssembly;

        private List<string> _referencedAssemblies = new List<string>();
        private string _msCorLib;

        private CodeTypeItem _currentCodeType;
        private DeviceItem _currentDevice;

        private string _defaultUsingStatements;
        public string DefaultUsingStatements {
            get { return _defaultUsingStatements; }
            set {
                if (value != _defaultUsingStatements) {
                    _defaultUsingStatements = value;
                    OnPropertyChanged("DefaultUsingStatements");
                }
            }
        }

        private string _extraUsingStatements;
        public string ExtraUsingStatements {
            get { return _extraUsingStatements; }
            set {
                if (value != _extraUsingStatements) {
                    _extraUsingStatements = value;
                    OnPropertyChanged("ExtraUsingStatements");
                    _currentWrapText = EditorHelpers.GetWrapText(_currentCodeType.CodeType, _currentDevice.DeviceType, ExtraUsingStatementsList);
                    CodeEditor.Document.SetHeaderAndFooterText(WrapHeader, WrapFooter);
                }
            }
        }

        private List<string> ExtraUsingStatementsList {
            set { 
                var extras = value;
                var sb = new StringBuilder();
                foreach (var s in extras)
                {
                    sb.Append(s).Append(Environment.NewLine);
                }
                ExtraUsingStatements = sb.ToString();
            }
            get {
                var extras = _extraUsingStatements == null || _extraUsingStatements.Trim() == ""
                                 ? new List<string>()
                                 : _extraUsingStatements.Split(new[] {Environment.NewLine}, StringSplitOptions.RemoveEmptyEntries).ToList();
                var isPixate = _currentDevice.PixateCssPaths != null;
                var pixate = "using PixateLib;";
                if (isPixate && !extras.Contains(pixate)) {
                    extras.Add(pixate);
                    var sb = new StringBuilder();
                    foreach (var s in extras) 
                    {
                        sb.Append(s).Append(Environment.NewLine);
                    }
                    ExtraUsingStatements = sb.ToString();
                }
                return extras;
            }
        }
        
        private bool LocalMode {
            get { return _currentDevice.DeviceType == DeviceTypes.Local; }
        }

        private string WrapHeader {
            get {
                if (_currentWrapText == null || _currentWrapText.Trim() == "")
                    return "";
                return _currentWrapText.Split(new[] {EditorHelpers.CodeTemplateStatementsPlaceHolder}, StringSplitOptions.None)[0];
            }
        }

        private string WrapFooter {
            get {
                if (_currentWrapText == null || _currentWrapText.Trim() == "")
                    return "";
                return _currentWrapText.Split(new[] {EditorHelpers.CodeTemplateStatementsPlaceHolder}, StringSplitOptions.None)[1];
            }
        }

        public enum DeviceTypes {
            Android,
            iOS,
            Local
        }

        public class DeviceItem {
            public string DeviceName { get; set; }
            public string DeviceAddress { get; set; }
            public string MainXamarinAssemblyName { get; set; }
            public string[] PixateCssPaths { get; set; }

            public DeviceTypes DeviceType;
        }

        public enum CodeTypes {
            Expression,
            Statements,
            Program,
            PixateCssFile,
        }

        private readonly List<CodeTypeItem> _defaultCodeTypeItems;

        public class CodeTypeItem {
            public string DisplayName { get; set; }
            public CodeTypes CodeType;
            public string EditFilePath;
        }

        public MainWindow() {
            InitializeComponent();

            _defaultCodeTypeItems = new List<CodeTypeItem> {
                new CodeTypeItem {DisplayName = "C# Expresssion", CodeType = CodeTypes.Expression},
                new CodeTypeItem {DisplayName = "C# Statements", CodeType = CodeTypes.Statements},
                new CodeTypeItem {DisplayName = "C# Program", CodeType = CodeTypes.Program},
            };

            _currentDevice = new DeviceItem {
                DeviceAddress = "__LOCAL__",
                DeviceType = DeviceTypes.Local,
                DeviceName = "Local"
            };
            _currentCodeType = _defaultCodeTypeItems[1];
        }

        #region Event handlers

        private void Window_Loaded(object sender, RoutedEventArgs e) {
            UpdateSendButtons();
            InitializeEditor();
            ConnectToApp(_currentDevice);
            InitializeResultWindow();
            InitializeProjectList();
        }

        private void SendCodeButton_Click(object sender, RoutedEventArgs e) {

            var result = SendCode(_currentDevice.DeviceAddress);
            if (result == null) return;
            var errorMessage = result.ErrorMessage;
            if (!String.IsNullOrWhiteSpace(errorMessage)) {
                if (errorMessage.StartsWith("___EXCEPTION_____At offset: ")) {
                    var exceptionBody = errorMessage.Substring("___EXCEPTION_____At offset: ".Length);
                    var exceptionParts = exceptionBody.Split(new[] {"__"}, StringSplitOptions.None);
                    var codeOffset = int.Parse(exceptionParts[0]) - 1;

                    var position = CodeEditor.Document.CurrentSnapshot.OffsetToPosition(codeOffset);

                    ShowLineError(position.Line + 1, exceptionParts[1]);
                }
                LogToResultsWindow(errorMessage);
            } else if (result.Results != null) {
                _htmlHolder.innerHTML = String.Join("", result.Results.Select( r => "<h1>" + r.ResultKey + "</h1>" + DumpToXhtml.Dump(r.ResultValue, 0).ToString()));
                _htmlWindow.execScript("Update();", "javascript");
            }
        }

        private void SendCssButton_Click(object sender, RoutedEventArgs e) {
            var cssText = CodeEditor.Document.CurrentSnapshot.Text;
            var cssData = Encoding.UTF8.GetBytes(cssText);
            var cssFilePathData = Encoding.UTF8.GetBytes(_currentCodeType.EditFilePath);
            var requestLength = 2 + cssFilePathData.Length + cssData.Length;
            var requestData = new byte[requestLength];
            var cssFilePathDataLength = (ushort) (cssFilePathData.Length);
            requestData[0] = (byte) (cssFilePathDataLength >> 8);
            requestData[1] = (byte) cssFilePathDataLength;
            Array.Copy(cssFilePathData, 0, requestData, 2, cssFilePathDataLength);
            Array.Copy(cssData, 0, requestData, 2 + cssFilePathDataLength, cssData.Length);
            SimpleHttpServer.SendPostRequest(_currentDevice.DeviceAddress, requestData, "UpdatePixateCSS");
        }

        private void LoadAssemblyButton_Click(object sender, RoutedEventArgs e) {
            if (_currentDevice == null) {
                MessageBox.Show("Please connect to an app first!");
                return;
            }
            var dlg = new Microsoft.Win32.OpenFileDialog {DefaultExt = ".dll|.exe"};

            var frameworkReferenceAssembliesDirectory = Path.GetDirectoryName(_referencedAssemblies.First());
            switch (_currentDevice.DeviceType) {
                case DeviceTypes.Android:
                    dlg.Filter =
                        "Xamarin.Android-compatible assembly (.dll)|*.dll|Xamarin.Android-compatible assembly (.exe)|*.exe";
                    dlg.InitialDirectory = Path.Combine(frameworkReferenceAssembliesDirectory, "MonoAndroid");
                    break;
                case DeviceTypes.iOS:
                    dlg.Filter =
                        "Xamarin.iOS-compatible assembly (.dll)|*.dll|Xamarin.iOS-compatible assembly (.exe)|*.exe";
                    dlg.InitialDirectory = Path.Combine(frameworkReferenceAssembliesDirectory, @"MonoTouch\v4.0");
                    break;
                case DeviceTypes.Local:
                    dlg.Filter = ".Net assembly (.dll)|*.dll|.Net assembly (.exe)|*.exe";
                    dlg.InitialDirectory = Path.Combine(frameworkReferenceAssembliesDirectory, @".NETFramework");
                    break;
            }

            var result = dlg.ShowDialog();
            if (!result.Value) return;
            var assemblyPath = dlg.FileName;
            _projectAssembly.AssemblyReferences.AddFrom(assemblyPath);
            _referencedAssemblies.Add(assemblyPath);
            if (!LocalMode)
                SimpleHttpServer.SendPostRequest(_currentDevice.DeviceAddress, File.ReadAllBytes(assemblyPath), "ExecuteAssembly");

            CompileAndAddReferences();
        }

        private void CodeTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var newCodeType = CodeTypeComboBox.SelectedItem as CodeTypeItem;
            if (newCodeType != _currentCodeType && newCodeType != null) {
                // Todo: save old cs/css file for load back later
                if (_currentCodeType.CodeType != CodeTypes.PixateCssFile) {
                    SaveEditorSource();
                    SavedExtraUsingSource(_currentCodeType.CodeType);
                }   

                _currentCodeType = newCodeType;
                SetText(true);

                /*
                if (newCodeType.CodeType != CodeTypes.PixateCssFile && _extraSourceFilesCollection.Count > 0)
                    CompileAndAddReferences();
                */
            }
            UpdateSendButtons();
        }

        private void ClearSimulatorWindowButton_Click(object sender, RoutedEventArgs e)
        {
            var wrapText = EditorHelpers.GetWrapText(CodeTypes.Statements, _currentDevice.DeviceType, null);
            var clearCode = wrapText.Replace("__STATEMENTSHERE__", _currentDevice.DeviceType == DeviceTypes.iOS ? EditorHelpers.ClearWindowStatements_iOS : EditorHelpers.ClearWindowStatements_Android);
            SendCode(_currentDevice.DeviceAddress, false, clearCode);            
        }

        private void AboutHelpButton_Click(object sender, RoutedEventArgs e)
        {
            (new AboutWindow()).Show();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var connectWindow = new ConnectWindow();
            var result = connectWindow.ShowDialog().Value;
            if (result)
            {
                ConnectToApp(connectWindow.SelectedDeviceItem);
            }
        }

        #endregion


        private ExecuteResponse SendCode(string url, bool wrapWithDefaultCode = true, string specialNonEditorCode = null) {
            var assemblyPath = CompileSource(wrapWithDefaultCode, specialNonEditorCode);
            if (String.IsNullOrWhiteSpace(assemblyPath)) return null;
            if (_currentDevice.DeviceType == DeviceTypes.Local) {
                var executeResponse = EditorHelpers.ExecuteLoadedAssemblyString(File.ReadAllBytes(assemblyPath));
                var dumpValues = executeResponse.GetDumpValues();
                if (dumpValues != null) {
                    executeResponse.Results = dumpValues.Select( v => new ResultPair(v.Description, Dumper.ObjectToDumpValue(v.Value, v.Level, executeResponse.GetMaxEnumerableItemCount()))).ToList();
                }
                return executeResponse;
            }
            var responseString = SimpleHttpServer.SendPostRequest(url, File.ReadAllBytes(assemblyPath), "ExecuteAssembly");
            return String.IsNullOrWhiteSpace(responseString) ? null : UtilityMethods.JsonDecode<ExecuteResponse>(responseString.Trim());
        }

        /// <summary>
        /// Get text offsets for all regular statements in the code.
        /// These will be used to insert special 'offset registration' statements
        /// Offset registrations are used to catch the location where runtime errors occur
        /// </summary>
        private static bool VisitNodesAndSelectStatementOffsets(IAstNode node, ICollection<int> statementOffsets)
        {
            if (node.Value == "SimpleName: \"DumpHelpers\"" && node.Parent.Value == "ClassDeclaration")
            {
                return false;
            }

            if (node is Statement && !(node is BlockStatement) && node.StartOffset.HasValue && node.StartOffset >= 0)
            {
                var isDumpMethodStatement = false;
                if (node.Parent != null && node.Parent.Parent != null)
                {
                    var methodNode = node.Parent.Parent as MethodDeclaration;
                    if (methodNode != null)
                    {
                        if (methodNode.Name.Text == "____TrackStatementOffset") isDumpMethodStatement = true;
                    }
                }

                if (!isDumpMethodStatement) statementOffsets.Add(node.StartOffset.Value);
            }
            return node.Children.All(childNode => VisitNodesAndSelectStatementOffsets(childNode, statementOffsets));
        }

        private string GetSourceWithOffsetRegistrationStatements()
        {
            var parseData = CodeEditor.Document.ParseData as IDotNetParseData;

            var statementOffsets = new List<int>();
            VisitNodesAndSelectStatementOffsets(parseData.Ast, statementOffsets);

            var inserts = statementOffsets.ToDictionary(o => o, o => String.Format("____TrackStatementOffset({0});", o));

            var documentWithOffsets = new EditorDocument();
            documentWithOffsets.SetText(parseData.Snapshot.Text);


            var options = new TextChangeOptions { OffsetDelta = TextChangeOffsetDelta.SequentialOnly };
            var change = documentWithOffsets.CreateTextChange(TextChangeTypes.Custom, options);
            foreach (var insert in inserts)
            {
                change.InsertText(insert.Key, insert.Value);
            }
            change.Apply();
            return documentWithOffsets.Text;
        }

        private string CompileSource(bool wrapWithDefaultCode, string specialNonEditorCode = null) 
        {
            if (_currentCodeType.CodeType == CodeTypes.PixateCssFile)
                return null;

            var codeWithOffsets = (specialNonEditorCode ?? GetSourceWithOffsetRegistrationStatements()).Replace("void Main(", "public void Main(");

            var sourceCode = wrapWithDefaultCode ? String.Format("{0}{1}{2}", WrapHeader.Replace("void Main(", "public void Main("), codeWithOffsets, WrapFooter) : codeWithOffsets;

            var useRegularMsCorLib = String.IsNullOrWhiteSpace(_msCorLib);
            var cpd = new CSharpCodeProvider();

            var compilerParameters = useRegularMsCorLib ? new CompilerParameters() : new CompilerParameters { CompilerOptions = "/nostdlib" };

            if (!useRegularMsCorLib) compilerParameters.ReferencedAssemblies.Add(_msCorLib);
            compilerParameters.ReferencedAssemblies.AddRange(_referencedAssemblies.ToArray());

            compilerParameters.GenerateExecutable = false;

            CompilerResults compileResults;

            if (specialNonEditorCode != null) {
                compileResults = cpd.CompileAssemblyFromSource(compilerParameters, sourceCode);
                if (_extraSourceFilesCollection.Count > 0) 
                {
                    var result = CompileSource(false);
                    if (result != null) {
                        _projectAssembly.AssemblyReferences.AddFrom(result);
                    }
                }   
            } else 
            {
                var sourceList = _extraSourceFilesCollection.Select(s => s.FilePath).ToList();
                sourceList.Add ( SaveEditorSource(true) );
                compileResults = cpd.CompileAssemblyFromFile(compilerParameters, sourceList.ToArray());
            }


            CodeEditor.Document.IndicatorManager.Clear<ErrorIndicatorTagger, ErrorIndicatorTag>();
            var errorStringBuilder = new StringBuilder();
            foreach (CompilerError error in compileResults.Errors) {
                var startLines = WrapHeader.Split('\n').Length;
                var codeLineNumber =  error.Line - startLines;     
                
                ShowLineError(codeLineNumber, error.ErrorText);
                errorStringBuilder.AppendFormat("Error on File <i>{0}</i> line <b>{1}</b>: <font color='red'>{2}</font> <br /><br />", error.FileName, codeLineNumber, error.ErrorText);
            }
            if (!String.IsNullOrWhiteSpace(errorStringBuilder.ToString())) 
                _htmlHolder.innerHTML = errorStringBuilder.ToString();
            else {
                _htmlHolder.innerHTML = String.Format("Compiled{0} successfully!", (specialNonEditorCode != null || _extraSourceFilesCollection.Count == 0) ? "" : " " + _extraSourceFilesCollection.Count + " files" );
            }
            //return compileResults.Errors.Count > 0 ? null : compilerParameters.OutputAssembly;
            if (compileResults.Errors.Count > 0) return null;

            var addResultToReference = specialNonEditorCode != null && _extraSourceFilesCollection.Count > 0;
            if (addResultToReference)
                _projectAssembly.AssemblyReferences.AddFrom(compilerParameters.OutputAssembly);
            return compilerParameters.OutputAssembly;
        }
        
        private void ShowLineError(int codeLineNumber, string errorMessage)
        {
            if (codeLineNumber < 0 || codeLineNumber >= CodeEditor.ActiveView.CurrentSnapshot.Lines.Count) codeLineNumber = 0;
            var editorLine = CodeEditor.ActiveView.CurrentSnapshot.Lines[codeLineNumber];
            CodeEditor.ActiveView.Selection.StartOffset = editorLine.StartOffset;
            CodeEditor.ActiveView.Selection.SelectToLineEnd();

            var tag = new ErrorIndicatorTag { ContentProvider = new PlainTextContentProvider(errorMessage) };
            CodeEditor.Document.IndicatorManager.Add<ErrorIndicatorTagger, ErrorIndicatorTag>(CodeEditor.ActiveView.Selection.SnapshotRange, tag);
        }

        private void UpdateCodeTypesComboBox()
        {
            CodeTypeComboBox.Items.Clear();
            _defaultCodeTypeItems.ForEach(i=>CodeTypeComboBox.Items.Add(i));
            if (_currentDevice.PixateCssPaths == null)
            {
                CodeTypeComboBox.SelectedItem = _currentCodeType;
            }
            else
            {
                foreach (var cssPath in _currentDevice.PixateCssPaths)
                {
                    var fileName = Path.GetFileNameWithoutExtension(cssPath);
                    CodeTypeComboBox.Items.Add(new CodeTypeItem { DisplayName = "Pixate CSS: " + fileName, CodeType = CodeTypes.PixateCssFile, EditFilePath = cssPath });
                }
                CodeTypeComboBox.SelectedItem = _currentCodeType;
            }            
        }

        private void UpdateSendButtons()
        {
            SendCssButton.Visibility = _currentCodeType.CodeType == CodeTypes.PixateCssFile ? Visibility.Visible : Visibility.Collapsed;
            SendCodeButton.Visibility = _currentCodeType.CodeType == CodeTypes.PixateCssFile ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ConnectToApp(DeviceItem deviceItem)
        {
            _currentDevice = deviceItem;

            var isLocal = _currentDevice.DeviceType == DeviceTypes.Local;

            ClearSimulatorWindowButton.IsEnabled = !isLocal;

            if (isLocal)
            {
                LogToResultsWindow("Running locally (regular .Net)");
            }
            else
            {
                _currentDevice.MainXamarinAssemblyName = SimpleHttpServer.SendCustomCommand(_currentDevice.DeviceAddress, "GetMainXamarinAssembly");
                if (_currentDevice.DeviceType == DeviceTypes.iOS)
                {
                    var cssFilesJson = SimpleHttpServer.SendCustomCommand(_currentDevice.DeviceAddress, "GetPixateCssFiles");
                    _currentDevice.PixateCssPaths = UtilityMethods.JsonDecode<string[]>(cssFilesJson);
                }
                LogToResultsWindow("Connected to device '{0}' on [{1}]", _currentDevice.DeviceName, _currentDevice.DeviceAddress);
            }

            UpdateCodeTypesComboBox();

            Title = String.Format("ProtoPad - {0}", _currentDevice.DeviceName);

            SetText(true);

            SendCodeButton.IsEnabled = true;
            LoadAssemblyButton.IsEnabled = true;

            StatusLabel.Content = "";

            if (_currentDevice.DeviceType != DeviceTypes.iOS)
            {
                return; // todo: locate and provide Android Emulator file path if applicable
            }

            var wrapText = EditorHelpers.GetWrapText(CodeTypes.Expression, _currentDevice.DeviceType, null);
            var getFolderCode = wrapText.Replace("__STATEMENTSHERE__", "Environment.GetFolderPath(Environment.SpecialFolder.Personal)");
            var result = SendCode(_currentDevice.DeviceAddress, false, getFolderCode);
            if (result == null || result.Results == null) return;
            var folder = result.Results.FirstOrDefault();
            if (folder == null) return;
            StatusLabel.Content = folder.ResultValue.PrimitiveValue.ToString();
        }

        private void SetText(bool reloadReferences)
        {
            if (_currentDevice == null) return;
            UpdateEditorLanguage(_currentCodeType.CodeType, reloadReferences);
            if (!String.IsNullOrWhiteSpace(_currentCodeType.EditFilePath))
            {
                var filePathData = Encoding.UTF8.GetBytes(_currentCodeType.EditFilePath);
                var fileContentsDataString = SimpleHttpServer.SendPostRequest(_currentDevice.DeviceAddress, filePathData, "GetFileContents");
                _currentWrapText = "";
                CodeEditor.Document.SetText(fileContentsDataString);
                CodeEditor.Document.SetHeaderAndFooterText("", "");
                CodeEditor.Document.FileName = Path.GetFileName(_currentCodeType.EditFilePath);
            }
            else {
                ExtraUsingStatementsList = LoadExraUsingStatements(_currentCodeType.CodeType);
                _currentWrapText = EditorHelpers.GetWrapText(_currentCodeType.CodeType, _currentDevice.DeviceType, ExtraUsingStatementsList);
                CodeEditor.Document.SetHeaderAndFooterText(WrapHeader, WrapFooter);
                var saved = LoadSavedEditorSource(_currentCodeType.CodeType);
                if (saved != "") {
                    CodeEditor.Document.SetText(saved);
                } else {
                    CodeEditor.Document.SetText(EditorHelpers.GetDefaultCode(_currentCodeType.CodeType, _currentDevice.DeviceType));
                }
                
                CodeEditor.Document.FileName = "ProtoPad.cs";   

                DefaultUsingStatements = EditorHelpers.GetDefaultUsingStatements(_currentDevice.DeviceType);
            }            
        }

        private void LogToResultsWindow(string message, params object[] stringFormatArguments)
        {
            if (_htmlHolder == null) return;
            var formattedMessage = String.Format(message, stringFormatArguments);
            _htmlHolder.innerHTML = formattedMessage;
        }

        private void InitializeResultWindow()
        {
            ResultTextBox.Navigated += (sender, args) =>
            {
                var htmlDocument3 = ResultTextBox.Document as IHTMLDocument3;
                var htmlDocument2 = ResultTextBox.Document as IHTMLDocument2;
                _htmlHolder = htmlDocument3.getElementById("wrapallthethings");
                _htmlWindow = htmlDocument2.parentWindow;
            };
            ResultTextBox.NavigateToString(Properties.Resources.ResultHtmlWrap);
        }

        private void InitializeEditor()
        {
            //UpdateEditorLanguage(_currentCodeType.CodeType, false);

            CodeEditor.PreviewKeyDown += (sender, args) =>
            {
                if (args.Key != Key.F5) return;
                SendCodeButton_Click(null, null);
                args.Handled = true;
            };
        }

        private void UpdateEditorLanguage(CodeTypes codeType, bool reloadAssemblies)
        {
            UpdateAssemblyReferences();
            var changeToCsharp = codeType != CodeTypes.PixateCssFile;
            var currentEditorLanguageIsCsharp = CodeEditor.Document.Language is CSharpSyntaxLanguage;
            if (currentEditorLanguageIsCsharp == changeToCsharp)
            {
                if (reloadAssemblies)
                {
                    LoadEditorReferences();  
                }
                return;
            }

            ISyntaxLanguage language;
            if (codeType == CodeTypes.PixateCssFile)
            {
                var serializer = new SyntaxLanguageDefinitionSerializer();
                using (var cssLanguageStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProtoPad_Client.Css.langdef"))
                {
                    language = serializer.LoadFromStream(cssLanguageStream);    
                }
            }
            else
            {
                // Initialize the project assembly (enables support for automated IntelliPrompt features)
                _projectAssembly = new CSharpProjectAssembly("ProtoPad Client");
                LoadEditorReferences();

                // Load the .NET Languages Add-on C# language and register the project assembly on it
                language = new CSharpSyntaxLanguage();
                language.RegisterProjectAssembly(_projectAssembly);
            }

            CodeEditor.Document.Language = language;
            CodeEditor.Document.Language.RegisterService(new IndicatorQuickInfoProvider());
        }

        private void UpdateAssemblyReferences()
        {
            switch (_currentDevice.DeviceType)
            {
                case DeviceTypes.Android:
                    _referencedAssemblies = EditorHelpers.GetXamarinAndroidBaseAssemblies(_currentDevice.MainXamarinAssemblyName, out _msCorLib);
                    break;
                case DeviceTypes.iOS:
                    var includePixate = _currentDevice.PixateCssPaths != null;
                    _referencedAssemblies = EditorHelpers.GetXamariniOSBaseAssemblies(_currentDevice.MainXamarinAssemblyName, out _msCorLib);
                    if (includePixate)
                    {
                        var pixateDllPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Binaries", "Pixate.dll");
                        if (File.Exists(pixateDllPath)) _referencedAssemblies.Add(pixateDllPath);
                    }
                    break;
                default:
                case DeviceTypes.Local:
                    _msCorLib = null;
                    _referencedAssemblies = EditorHelpers.GetRegularDotNetBaseAssemblyNames();
                    break;
            }
        }

        private readonly Dictionary<string, IProjectAssemblyReference> cachedReferences = new Dictionary<string, IProjectAssemblyReference>();

        private void LoadEditorReferences()
        {            
            _projectAssembly.AssemblyReferences.Clear();     
            LogToResultsWindow("Loading autocompletion and assembly data");
            new Task(() =>
                {
                    _projectAssembly.AssemblyReferences.AddMsCorLib();
                    foreach (var assembly in _referencedAssemblies)
                    {
                        if (cachedReferences.ContainsKey(assembly))
                        {
                            _projectAssembly.AssemblyReferences.Add(cachedReferences[assembly]);
                        }
                        else
                        {
                            cachedReferences[assembly] = _projectAssembly.AssemblyReferences.AddFrom(assembly);
                        }
                    }
                    Dispatcher.Invoke((Action)(() => LogToResultsWindow("FINISHED loading autocompletion and assembly data")));

                }).Start();
        }

        #region DataBinding Support
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName) {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion

        #region Save and Load
        private string SaveEditorSource(bool forCompileOnly = false)
        {

            if (forCompileOnly)
            {
                var tmpFile = CodeEditor.Document.FileName + ".tmp";
                var sourceCode = (WrapHeader + CodeEditor.Document.CurrentSnapshot.Text + WrapFooter).Replace("void Main(", "public void Main(");
                File.WriteAllText(tmpFile, sourceCode);
                return tmpFile;
            }

            var codeTypeString = Enum.GetName(_currentCodeType.CodeType.GetType(), _currentCodeType.CodeType);
            var savePath = CodeEditor.Document.FileName + "." + codeTypeString;
            if (_workingProject != null)
            {
                savePath = Path.Combine(_workingProject.ProjectPath, savePath);
            }
            CodeEditor.Document.SaveFile(savePath, Encoding.UTF8, LineTerminator.CarriageReturnNewline);
            return savePath;
        }

        private string LoadSavedEditorSource(CodeTypes codeType)
        {
            var codeTypeString = Enum.GetName(_currentCodeType.CodeType.GetType(), _currentCodeType.CodeType);
            var savePath = CodeEditor.Document.FileName + "." + codeTypeString;
            if (_workingProject != null)
            {
                savePath = Path.Combine(_workingProject.ProjectPath, savePath);
            }
            if (File.Exists(savePath))
            {
                return File.ReadAllText(savePath);
            }
            return "";
        }

        private void SavedExtraUsingSource(CodeTypes codeType)
        {
            var codeTypeString = Enum.GetName(_currentCodeType.CodeType.GetType(), _currentCodeType.CodeType);
            var savePath = CodeEditor.Document.FileName + "." + codeTypeString + ".using";
            if (_workingProject != null)
            {
                savePath = Path.Combine(_workingProject.ProjectPath, savePath);
            }
            var sb = new StringBuilder();
            foreach (var item in ExtraUsingStatementsList)
            {
                sb.Append(item).Append(Environment.NewLine);
            }
            File.WriteAllText(savePath, sb.ToString());
        }

        private List<string> LoadExraUsingStatements(CodeTypes codeType)
        {
            var codeTypeString = Enum.GetName(_currentCodeType.CodeType.GetType(), _currentCodeType.CodeType);
            var savePath = CodeEditor.Document.FileName + "." + codeTypeString + ".using";
            if (_workingProject != null)
            {
                savePath = Path.Combine(_workingProject.ProjectPath, savePath);
            }
            if (File.Exists(savePath))
            {
                return new List<string>(File.ReadAllLines(savePath));
            }

            return new List<string>();
        }

        #endregion

        #region [Feature] Extra Source Files Support

        public class SourceFile
        {
            public SourceFile(string path) 
            {
                FileName = Path.GetFileName(path);
                FilePath = Path.GetFullPath(path);
            }
            public string FileName { get; set; }
            public string FilePath { get; set; }
        }

        private ObservableCollection<SourceFile> _extraSourceFilesCollection = new ObservableCollection<SourceFile>();
        public ObservableCollection<SourceFile> ExtraSourceFilesCollection 
        {
            get {
                return _extraSourceFilesCollection;
            }
        }

        private void AddSourceFiles(string  filesOrFolder) 
        {
            if (filesOrFolder == null || filesOrFolder.Trim() == "") return;

            var items = filesOrFolder.Split(new[] { ProtoPadConfig.kFileListDelimeter }, StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach ( var item in items ) 
            {
                if ( Directory.Exists(item) ) 
                {
                    foreach (var path in Directory.EnumerateFiles(item, "*.*", SearchOption.AllDirectories).Where(s => Path.GetExtension(s).ToLower() == ".cs")) 
                    {
                        if (_extraSourceFilesCollection.Count(s => s.FilePath == path) <= 0) 
                        {
                            _extraSourceFilesCollection.Add(new SourceFile(path));
                        }
                    }
                } else if (File.Exists(item)) 
                {
                    if (_extraSourceFilesCollection.Count(s => s.FilePath == item) <= 0) 
                    {
                        _extraSourceFilesCollection.Add(new SourceFile(item));
                    }
                }
            }

            SaveSourceListToConfig();
            CompileAndAddReferences();
        }

        private void CompileAndAddReferences()
        {
            var assembly = CompileSource(true);
            if (assembly != null)
            {
                _projectAssembly.AssemblyReferences.AddFrom(assembly);
            }
        }
       
        private void RemoveSourceButton_Click(object sender, RoutedEventArgs e)
        {
            while (ExtraSourceListView.SelectedItems.Count>0) {
                (ExtraSourceListView.ItemsSource as IList).Remove(ExtraSourceListView.SelectedItems[0]);
            }
            SaveSourceListToConfig();
            CompileAndAddReferences();
        }

        private void SaveSourceListToConfig() {

            var sb = new StringBuilder();
            _extraSourceFilesCollection.ToList().ForEach(s => sb.Append(s.FilePath).Append(ProtoPadConfig.kFileListDelimeter));
            ProtoPadConfig.SaveConfig(ProtoPadConfig.ConfigItemType.Sources, sb.ToString());
        }

        private void ExtraSourceFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog() {
                DefaultExt = "*.cs",
                Multiselect = true,
            };
            dlg.Filter = "C# Source Files (.cs)|*.cs";
            var result = dlg.ShowDialog();
            if (!result.Value) return;
            var sb = new StringBuilder();
            foreach (var fileName in dlg.FileNames) {
                sb.Append(fileName).Append(ProtoPadConfig.kFileListDelimeter);
            }
            AddSourceFiles(sb.ToString());
        }

        private void ExtraSourceFolderButton_Click(object sender, RoutedEventArgs e) 
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog()) 
            {
                if (_workingProject != null)
                    dlg.SelectedPath = _workingProject.ProjectPath;
                else 
                    dlg.SelectedPath = AppDomain.CurrentDomain.BaseDirectory;    
                
                var result = dlg.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK) 
                {
                    var folder = dlg.SelectedPath;
                    AddSourceFiles(folder);
                }
            }
        }

        private void ExtraSourceListView_DoubleClick(object sender, MouseButtonEventArgs e) 
        {
            var source = ((ListViewItem) sender).Content as SourceFile;
            //todo: Open ExtraSource for editing (either in anothoter SyntaxEditor tab or in thirdparty tool
            e.Handled = true;
        }

        #endregion

        #region [Feature] Projects Support

        private ProtoPadProject _workingProject;

        private ObservableCollection<ProtoPadProject> _projectsCollection = new ObservableCollection<ProtoPadProject>();
        public ObservableCollection<ProtoPadProject> ProjectsCollection
        {
            get
            {
                return _projectsCollection;
            }
        }

        private void InitializeProjectList() 
        {
            var projectListString = ProtoPadConfig.LoadConfig(ProtoPadConfig.ConfigItemType.Projects);
            if (projectListString == null || projectListString.Trim() == "")
                return;

            var projectList = projectListString.Split(new[] {'|'}, StringSplitOptions.RemoveEmptyEntries).ToList().Select(s=>new ProtoPadProject(s)).ToList();
            if ( projectList != null && projectList.Count() > 0 )
                projectList.ForEach( p => _projectsCollection.Add(p) );
        }

        private void AddProject(string projectFolder)
        {
            if (projectFolder == null || projectFolder.Trim() == "") return;
            if (!Directory.Exists(projectFolder)) return;

            if (_projectsCollection.Count(s => s.ProjectPath == projectFolder) <= 0)
                _projectsCollection.Add(new ProtoPadProject(projectFolder));

            SaveProjectListToConfig();
        }

        private void AddProjectButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.SelectedPath = AppDomain.CurrentDomain.BaseDirectory;
                var result = dlg.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    var folder = dlg.SelectedPath;
                    AddProject(folder);
                }
            }
        }

        private void RemoveProjectButton_Click(object sender, RoutedEventArgs e) 
        {
            while (ProjectListView.SelectedItems.Count > 0){
                (ProjectListView.ItemsSource as IList).Remove(ProjectListView.SelectedItems[0]);
            }

            SaveProjectListToConfig();
        }

        private void SaveProjectListToConfig() 
        {
            var sb = new StringBuilder();
            _projectsCollection.ToList().ForEach(p => sb.Append(p.ProjectPath).Append("|"));
            ProtoPadConfig.SaveConfig(ProtoPadConfig.ConfigItemType.Projects, sb.ToString());
        }

        private void SwitchToWorkingProject(ProtoPadProject workingProject) 
        {
            SaveEditorSource();
            SavedExtraUsingSource(_currentCodeType.CodeType);

            _workingProject = workingProject;
            ProtoPadConfig.SetWorkingProject(_workingProject);
            SetText(true);

            _extraSourceFilesCollection.Clear();
            var sourceList = ProtoPadConfig.LoadConfig(ProtoPadConfig.ConfigItemType.Sources);
            if (sourceList != null)
                AddSourceFiles(sourceList);
            ExtraSourceTabItem.IsSelected = true;

            _referencesCollection.Clear();
            var refereceList = ProtoPadConfig.LoadConfig(ProtoPadConfig.ConfigItemType.References);
            if (refereceList!= null)
                AddReference(refereceList);
        }

        private void ProjectListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProjectListView.SelectedItems.Count <= 0) {
                SwitchToWorkingProject(null);
            }
        }

        private void SwitchProjectButton_Click(object sender, RoutedEventArgs e) 
        {
            var project = ProjectListView.SelectedItem as ProtoPadProject;
            if (project == null)
                return;
            SwitchToWorkingProject(project);
        }

        private void ProjectListView_DoubleClick(object sender, MouseButtonEventArgs e) 
        {
            var clicking = ((ListViewItem)sender).Content as ProtoPadProject;
            Console.WriteLine("Double Clicked: " + clicking.ProjectPath);
            SwitchProjectButton_Click(sender, null);
            e.Handled = true;
        }

        #endregion

        #region [Feature] References Management

        private ObservableCollection<ProtoPadReference> _referencesCollection = new ObservableCollection<ProtoPadReference>();
        public ObservableCollection<ProtoPadReference> ReferencesCollection
        {
            get
            {
                return _referencesCollection;
            }
        }

        public class ProtoPadReference : INotifyPropertyChanged 
        {
            public ProtoPadReference(string path)
            {
                ReferenceName = Path.GetFileName(path);
                ReferencePath = Path.GetFullPath(path);
                _loaded = false;
                AssemblyName = ResolveAssemblyName(ReferencePath);
            }

            private string ResolveAssemblyName(string path) {
                var fullName = Assembly.LoadFrom(path).FullName;
                var second = fullName.IndexOf(",", fullName.IndexOf(",") + 1);
                if (second == -1)
                    return fullName;
                return fullName.Substring(0, second);
            }

            private bool _loaded;
            public bool Loaded { 
                get { return _loaded; }
                set {  
                    if (value != _loaded) {
                        _loaded = value;
                        OnPropertyChanged("Loaded");
                    }
                }
            }
            public string ReferenceName { get; set; }
            public string ReferencePath { get; set; }
            public string AssemblyName { get; set; }

            #region DataBinding Support
            public event PropertyChangedEventHandler PropertyChanged;

            protected virtual void OnPropertyChanged(string propertyName)
            {
                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
            }
            #endregion
        }

        private void AddReference(string referenceFiles) {
            var referenceList = referenceFiles.Split(new [] {ProtoPadConfig.kFileListDelimeter}, StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var reference in referenceList) {
                if (_referencesCollection.Count(s => s.ReferencePath == reference) <= 0)
                    _referencesCollection.Add(new ProtoPadReference(reference));
            }

            SaveReferenceListToConfig();
            
        }

        private void AddReferenceButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog()
            {
                DefaultExt = "*.dll",
                Multiselect = true,
            };
            dlg.Filter = "Assembly DLL (.dll)|*.dll|Assembly EXE (.exe)|*.exe";
            var result = dlg.ShowDialog();
            if (!result.Value) return;
            var sb = new StringBuilder();
            foreach (var fileName in dlg.FileNames)
            {
                sb.Append(fileName).Append(ProtoPadConfig.kFileListDelimeter);
            }
            AddReference(sb.ToString());
        }

        private void RemoveReferenceButton_Click(object sender, RoutedEventArgs e)
        {
            
            while (ReferenceListView.SelectedItems.Count > 0)
            {
                (ReferenceListView.ItemsSource as IList).Remove(ReferenceListView.SelectedItems[0]);
            }

            SaveReferenceListToConfig();
        }

        private void SaveReferenceListToConfig()
        {
            var sb = new StringBuilder();
            _referencesCollection.ToList().ForEach(r => sb.Append(r.ReferencePath).Append(ProtoPadConfig.kFileListDelimeter));
            ProtoPadConfig.SaveConfig(ProtoPadConfig.ConfigItemType.References, sb.ToString());
        }

        private void ReferenceListView_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            var clicking = ((ListViewItem)sender).Content as ProtoPadReference;

            if (clicking.Loaded) {
                UnLoadReference(clicking);
            } else {
                LoadReference(clicking);
            }
            e.Handled = true;
        }

        private void UnLoadReference(ProtoPadReference aReference) {
            var found =
                _projectAssembly.AssemblyReferences.ToList()
                                .Where(a => a.AssemblyName.FullName == aReference.AssemblyName)
                                .ToList();
            if (found.Count > 0)
                found.ForEach(a => _projectAssembly.AssemblyReferences.Remove(a));

            if (_referencedAssemblies.Contains(aReference.ReferencePath)) {
                _referencedAssemblies.Remove(aReference.ReferencePath);
            }
            aReference.Loaded = false;
        }

        private void LoadReference(ProtoPadReference aReference) {
            _projectAssembly.AssemblyReferences.AddFrom(aReference.ReferencePath);
            if (!_referencedAssemblies.Contains(aReference.ReferencePath)) {
                _referencedAssemblies.Add(aReference.ReferencePath);
            }
            if (!LocalMode)
                SimpleHttpServer.SendPostRequest(_currentDevice.DeviceAddress, File.ReadAllBytes(assemblyPath),
                                                 "ExecuteAssembly");

            aReference.Loaded = true;
        }

        #endregion
    }

    public class ProtoPadProject
    {
        private const string kConfigFolderName = ".protopad";
        private const string kIniFilename = "project.ini";

        private string _configFolder;

        public ProtoPadProject(string path)
        {
            ProjectName = new DirectoryInfo(path).Name;
            ProjectPath = path;
            _configFolder = Path.Combine(ProjectPath, kConfigFolderName);
            if (!Directory.Exists(_configFolder)) Directory.CreateDirectory(_configFolder);
        }

        public string ProjectName { get; set; }
        public string ProjectPath { get; set; }

        public string Ini
        {
            get
            {
                var iniFullPath = Path.Combine(_configFolder, kIniFilename);
                return iniFullPath;
            }
        }
    }
    
    public static class ProtoPadConfig {
        
        private const string kProtoPadConfigFile = "protopad.ini";

        public const string kFileListDelimeter = "|";

        private const string kSectionConnect = "Connect";
        private const string kSectionProjects  = "Projects";
        private const string kSectionFiles = "Files";

        private static IniFile _protoPadIni = new IniFile(kProtoPadConfigFile);
        private static ProtoPadProject _project;
        private static IniFile _projectIni;

        public enum ConfigItemType
        {
            IP,
            PORT,
            Projects,
            Sources,
            References
        }

        public static void SetWorkingProject(ProtoPadProject p) 
        {
            _project = p;
            if (_project != null)
                _projectIni = new IniFile(_project.Ini);
        }

        private static IniFile GetActiveIniFile(ConfigItemType itemType) 
        {
            switch (itemType)
            {   
                // Project Level
                case ConfigItemType.IP:
                case ConfigItemType.PORT:
                case ConfigItemType.Sources:
                case ConfigItemType.References:
                    return (_project != null) ? _projectIni : _protoPadIni;
            }
            // Global Level
            return _protoPadIni;
        }

        public static void SaveConfig(ConfigItemType itemType, string value)
        {
            string section, key;
            GetSectionAndKey(itemType, out section, out key);

            var ini = GetActiveIniFile(itemType);
            ini.SetValue(section, key, value);
            ini.Save();
        }

        public static string LoadConfig(ConfigItemType type)
        {
            string section, key;
            GetSectionAndKey(type, out section, out key);
            var ini = GetActiveIniFile(type);
            return ini.GetValue(section, key);
        }

        private static void GetSectionAndKey(ConfigItemType itemType, out string section, out string key) 
        {
            section = "";
            switch (itemType) 
            {
                case ConfigItemType.IP:
                case ConfigItemType.PORT:
                    section = kSectionConnect;
                    break;
                case ConfigItemType.Sources:
                case ConfigItemType.References:
                    section = kSectionFiles;
                    break;
                case ConfigItemType.Projects:
                    section = kSectionProjects;
                    break;
            }
            key = Enum.GetName(itemType.GetType(), itemType);
        }
    }

}
