﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Ryr.SolutionHistory.Forms;
using Tanguy.WinForm.Utilities.DelegatesHelpers;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;
using System.ComponentModel.Composition;

namespace Ryr.SolutionHistory
{
    public partial class SolutionHistory : PluginControlBase
    {
        public SolutionHistory()
        {
            InitializeComponent();
        }

        private void RetrieveCurrentSolutions()
        {
            fromDateTimePicker.Value = toDateTimePicker.Value.AddDays(-7);
            solutionsListBox.Items.Clear();
            var solutionFetchXml = @"
                    <fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" distinct=""false"">
                      <entity name=""solution"" >
                        <attribute name=""friendlyname"" />
                        <attribute name=""uniquename"" />
                        <attribute name=""solutionid"" />
                        <attribute name=""isvisible"" />
                        <filter>
                          <condition attribute=""isvisible"" operator=""eq"" value=""1"" />
                        </filter>
                      </entity>
                    </fetch>";
            var solutions = Service.RetrieveMultiple(new FetchExpression(solutionFetchXml)).Entities;
            foreach (var solution in solutions)
            {
                solutionsListBox.Items.Add(solution.GetAttributeValue<string>("uniquename"));
            }
            Enumerable.Range(0, solutions.Count)
                .ToList().ForEach(x => solutionsListBox.SetSelected(x, true));
            solutionsListBox.TopIndex = 0;
        }

        private void RetrieveSolutionHistory()
        {
            if (solutionsListBox.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "No solutions selected.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
                return;
            }
            WorkAsync(new WorkAsyncInfo("Loading solution history..", (e) => {
                var solutionFilter = new StringBuilder();
                var solutions = new StringBuilder();
                foreach (var s in solutionsListBox.SelectedItems)
                {
                    solutionFilter.AppendFormat("<value>{0}</value>", s);
                }
                foreach (var s in solutionsListBox.Items)
                {
                    solutions.AppendFormat("<value>{0}</value>", s);
                }
                var importJobsFetchXml = string.Format(
                    @"<fetch version=""1.0"" output-format=""xml-platform"" mapping=""logical"" distinct=""false"">
                          <entity name=""importjob"">
                            <attribute name=""importjobid"" />
                            <attribute name=""solutionname"" />
                            <attribute name=""startedon"" />
                            <attribute name=""completedon"" />
                            <attribute name=""createdby"" />
                            <attribute name=""data"" />
                            <attribute name=""progress"" />
                            <order attribute=""startedon"" descending=""true"" />
                            <filter type=""and"">
                              <condition attribute=""name"" operator=""eq"" value=""Customizations"" />
                              <condition attribute=""completedon"" operator=""not-null"" />
                              <filter type=""or"">
                                  <condition attribute=""solutionname"" operator=""in"" >{0}</condition>
                                  {3}
                              </filter>
                              <condition attribute=""startedon"" operator=""on-or-after"" value=""{1}"" />
                              <condition attribute=""completedon"" operator=""on-or-before"" value=""{2}"" />
                            </filter>
                          </entity>
                        </fetch>", solutionFilter.ToString(),
                             fromDateTimePicker.Value.ToString("s"),
                             toDateTimePicker.Value.ToString("s"),
                             includeDeletedSolutionsCheckBox.Checked ?
                             string.Format(@"<condition attribute=""solutionname"" operator=""not-in"" >{0}</condition>", solutions)
                             : "");
                e.Result = Service.RetrieveMultiple(new FetchExpression(importJobsFetchXml)).Entities;
            }) { PostWorkCallBack = (completedargs) => {

                ListViewDelegates.ClearItems(lvSolutionImports);
                ListViewDelegates.ClearItems(lvSolutionComponents);
                ListViewDelegates.ClearItems(lvSolutionComponentDetail);
                foreach (var importJob in (ICollection<Entity>)completedargs.Result)
                {
                    var solutionXml = importJob.GetAttributeValue<string>("data");
                    var listItem = new ListViewItem
                    {
                        Text = importJob.GetAttributeValue<DateTime>("startedon").ToLocalTime().ToString("dd-MMM-yyyy HH:mm"),
                        Tag = solutionXml
                    };
                    listItem.SubItems.Add(importJob.GetAttributeValue<DateTime>("completedon").ToLocalTime().ToString("dd-MMM-yyyy HH:mm"));
                    var parsedComponentXml = XElement.Parse(solutionXml);
                    var solutionManifest = parsedComponentXml
                        .Elements(SolutionElement.SolutionManifests)
                        .Elements(SolutionElement.SolutionManifest).ToList();
                    var upgradeInformation = parsedComponentXml.Elements(SolutionElement.UpgradeSolutionPackageInformation).ToList();

                    var name =
                        solutionManifest
                            .Elements(SolutionElement.LocalizedNames)
                            .Elements(SolutionElement.LocalizedName)
                            .Attributes(SolutionAttribute.Description)
                            .Select(att => att.Value)
                            .FirstOrDefault() ?? importJob.GetAttributeValue<string>("solutionname");

                    listItem.SubItems.Add(name);
                    listItem.SubItems.Add(solutionManifest.Elements(SolutionElement.Version).Select(el => " " + el.Value).FirstOrDefault() ?? string.Empty);
                    listItem.SubItems.Add((solutionManifest.Elements(SolutionElement.Managed).Select(cv => cv.Value).FirstOrDefault() ?? string.Empty) == "0"
                        ? "Unmanaged"
                        : "Managed");
                    listItem.SubItems.Add(importJob.GetAttributeValue<EntityReference>("createdby").Name);
                    listItem.SubItems.Add(upgradeInformation.Elements(SolutionElement.CurrentVersion).Select(cv => cv.Value).FirstOrDefault() ?? string.Empty);
                    listItem.SubItems.Add(upgradeInformation.Elements(SolutionElement.FileVersion).Select(fv => fv.Value).FirstOrDefault() ?? string.Empty);
                    listItem.SubItems.Add(solutionManifest
                        .Elements(SolutionElement.Publisher)
                        .Elements(SolutionElement.LocalizedNames)
                        .Elements(SolutionElement.LocalizedName)
                        .Attributes(SolutionAttribute.Description)
                        .Select(att => att.Value).FirstOrDefault() ?? 
                        solutionManifest
                        .Elements(SolutionElement.Publisher)
                        .Elements(SolutionElement.UniqueName)
                        .Select(un => un.Value).FirstOrDefault() ?? 
                        string.Empty);
                    listItem.SubItems.Add(importJob.GetAttributeValue<double>("progress").ToString("0.##"));
                    listItem.SubItems.Add(importJob.GetAttributeValue<Guid>("importjobid").ToString());
                    ListViewDelegates.AddItem(lvSolutionImports, listItem);
                }
                lvSolutionImports.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            } });
        }

        private void tsbSolutionHistory_Click(object sender, EventArgs e)
        {
            tsbExportSolutionLog.Enabled = false;
            ExecuteMethod(RetrieveSolutionHistory);
        }

        private void tsbCloseThisTab_Click(object sender, EventArgs e)
        {
            base.CloseTool();
        }

        private void lvSolutionImports_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lvSolutionImports.SelectedItems.Count > 0)
            {
                tsbExportSolutionLog.Enabled = true;
                string importDataXml = lvSolutionImports.SelectedItems[0].Tag.ToString();
                WorkAsync(new WorkAsyncInfo("Parsing solution XML...", ParseSolutionXml)
                {
                    PostWorkCallBack = LoadParsedSolutionXmlToGrid, 
                    AsyncArgument = importDataXml
                });
            }
            else
            {
                tsbExportSolutionLog.Enabled = false;
            }

        }

        private void LoadParsedSolutionXmlToGrid(RunWorkerCompletedEventArgs args)
        {
            var itemsToTrack = (List<Tuple<string, IEnumerable<XElement>>>)args.Result;
            ListViewDelegates.ClearItems(lvSolutionComponents);
            foreach (var itemToTrack in itemsToTrack)
            {
                var listItem = new ListViewItem { Text = itemToTrack.Item1, Tag = itemToTrack.Item2 };
                var resultNodes = itemToTrack.Item2.Descendants(SolutionAttribute.Result).ToList();
                if (resultNodes.Any(x => x.Attribute(SolutionAttribute.Result) != null &&
                    (x.Attribute(SolutionAttribute.Result).Value.Equals(ComponentResult.Failures, StringComparison.InvariantCultureIgnoreCase) ||
                    x.Attribute(SolutionAttribute.Result).Value.Equals(ComponentResult.Errors, StringComparison.InvariantCultureIgnoreCase))))
                {
                    listItem.ForeColor = Color.Red;
                }
                if (resultNodes.Any(x => x.Attribute(SolutionAttribute.Result) != null &&
                    x.Attribute(SolutionAttribute.Result).Value.Equals(ComponentResult.Warnings, StringComparison.InvariantCultureIgnoreCase)))
                {
                    listItem.ForeColor = Color.Orange;
                }
                if (!itemToTrack.Item2.Any())
                {
                    ListViewDelegates.RemoveItem(lvSolutionComponents, listItem);
                }
                else
                {
                    listItem.SubItems.Add(itemToTrack.Item2.Count().ToString());
                    ListViewDelegates.AddItem(lvSolutionComponents, listItem);
                }
            }
            lvSolutionComponents.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
        }

        private void ParseSolutionXml(DoWorkEventArgs args)
        {
            ListViewDelegates.ClearItems(lvSolutionComponents);
            ListViewDelegates.ClearItems(lvSolutionComponentDetail);
            var parsedSolutionXml = XElement.Parse(args.Argument.ToString());
            var itemsToTrack = new List<Tuple<string, IEnumerable<XElement>>>();
            foreach (var field in typeof(SolutionComponent).GetFields())
            {
                itemsToTrack.Add(new Tuple<string, IEnumerable<XElement>>(field.Name,
                    parsedSolutionXml.Descendants(field.GetValue(null).ToString())));
            } 		
            args.Result = itemsToTrack;
        }

        private void lvSolutionComponents_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lvSolutionComponents.SelectedItems.Count > 0)
            {
                WorkAsync(new WorkAsyncInfo("Parsing component XML...", ParseComponentXml)
                {
                    PostWorkCallBack = LoadParsedComponentXmlToGrid, 
                    AsyncArgument = lvSolutionComponents.SelectedItems[0].Tag
                });
            }
        }

        private void LoadParsedComponentXmlToGrid(RunWorkerCompletedEventArgs args)
        {
            var itemsToTrack = (List<Tuple<string, string, IEnumerable<XElement>>>)args.Result;
            ListViewDelegates.ClearItems(lvSolutionComponentDetail);
            foreach (var itemToTrack in itemsToTrack)
            {
                var listItem = new ListViewItem
                {
                    Text = string.Empty, 
                    Tag = itemToTrack.Item3
                };
                listItem.SubItems.Add(itemToTrack.Item1);
                var importStatuses = itemToTrack.Item3.Where(x => x.Attribute(SolutionAttribute.Result) != null).ToList();
                if (importStatuses.Any(x => x.Attribute(SolutionAttribute.Result).Value == ComponentResult.Errors ||
                    x.Attribute(SolutionAttribute.Result).Value == ComponentResult.Failures))
                {
                    listItem.ImageIndex = 1;
                }
                else
                if (importStatuses.Any(x => x.Attribute(SolutionAttribute.Result).Value == ComponentResult.Warnings))
                {
                    listItem.ImageIndex = 2;
                }
                else
                {
                    listItem.ImageIndex = 0;
                }
                var messages = string.Join("\n",importStatuses.
                    Where(x => x.Attribute(SolutionAttribute.ErrorText) != null && 
                        x.Attribute(SolutionAttribute.ErrorText).Value != string.Empty)
                    .Select(x => x.Attribute(SolutionAttribute.ErrorText).Value));
                listItem.SubItems.Add(messages);
                ListViewDelegates.AddItem(lvSolutionComponentDetail, listItem);
            }
            lvSolutionComponentDetail.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
        }

        private void ParseComponentXml(DoWorkEventArgs args)
        {
            ListViewDelegates.ClearItems(lvSolutionComponentDetail);
            var parsedComponentXml = (IEnumerable<XElement>)args.Argument;
            var itemsToTrack = new List<Tuple<string, string, IEnumerable<XElement>>>();
            foreach (var xElement in parsedComponentXml)
            {
                var componentName = xElement.Attribute(SolutionAttribute.LocalizedName) ??
                                    xElement.Attribute(SolutionAttribute.Name);
                itemsToTrack.Add(new Tuple<string, string, IEnumerable<XElement>>(
                    componentName != null ? componentName.Value : string.Empty,
                    xElement.Attribute(SolutionAttribute.Id) != null ? xElement.Attribute(SolutionAttribute.Id).Value : string.Empty, 
                    xElement.Elements(SolutionAttribute.Result)));
            }
            args.Result = itemsToTrack;
        }

        private void lvSolutionComponentDetail_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (lvSolutionComponentDetail.SelectedItems.Count == 0)
                return;

            ListViewItem item = lvSolutionComponentDetail.SelectedItems[0];
            var errorsAndWarnings = (IEnumerable<XElement>)item.Tag;
            if (!errorsAndWarnings.Any(x => x.Attribute(SolutionAttribute.Result).Value == ComponentResult.Warnings ||
                x.Attribute(SolutionAttribute.Result).Value == ComponentResult.Errors || 
                x.Attribute(SolutionAttribute.Result).Value == ComponentResult.Failures))
            {
                return;
            }
            var dialog = new ErrorsAndWarningsDialog(errorsAndWarnings);
            dialog.ShowDialog(this);
        }

        private void tsbExportSolutionLog_Click(object sender, EventArgs e)
        {
            if (lvSolutionImports.SelectedItems.Count > 0)
            {
                var importJobId = new Guid(lvSolutionImports.SelectedItems[0].SubItems[10].Text);
                WorkAsync(new WorkAsyncInfo("Save Solution Import Log..", ExecuteExportLogRequest)
                {
                    PostWorkCallBack = ProcessExportLogResponse, 
                    AsyncArgument = importJobId
                });
            }
        }

        private void ProcessExportLogResponse(RunWorkerCompletedEventArgs args)
        {
            if (lvSolutionImports.SelectedItems.Count == 0)
            {
                return;
            }
            var dialog = new SaveFileDialog
            {
                Filter = "Excel XML Spreadsheet (*.xml)|*.xml",
                FileName = string.Format("{0}-{1}.xml",
                    lvSolutionImports.SelectedItems[0].SubItems[2].Text,
                    DateTime.Parse(lvSolutionImports.SelectedItems[0].SubItems[0].Text).ToString("yyyyMMdd"))
            };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                File.WriteAllText(dialog.FileName, args.Result.ToString());
                MessageBox.Show(this, "Successfully saved the import log file. Open using Excel.",
                    "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void ExecuteExportLogRequest(DoWorkEventArgs args)
        {
            var formattedImportJobResultsRequest = new RetrieveFormattedImportJobResultsRequest
            {
                ImportJobId = (Guid) args.Argument
            };
            var formattedImportJobResultsResponse = (RetrieveFormattedImportJobResultsResponse)Service.Execute(formattedImportJobResultsRequest);
            args.Result = formattedImportJobResultsResponse.FormattedResults;
        }

        private void lvSolutionImports_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            lvSolutionImports.SelectedItems.Clear();
            lvSolutionImports.Sorting = lvSolutionImports.Sorting == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
            lvSolutionImports.ListViewItemSorter = new ListViewItemComparer(e.Column, lvSolutionImports.Sorting);
        }

        private void lvSolutionComponentDetail_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            lvSolutionComponentDetail.SelectedItems.Clear();
            lvSolutionComponentDetail.Sorting = lvSolutionComponentDetail.Sorting == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
            lvSolutionComponentDetail.ListViewItemSorter = new ListViewItemComparer(e.Column, lvSolutionComponentDetail.Sorting);
        }

        private void SolutionHistory_Load(object sender, EventArgs e)
        {
            ExecuteMethod(RetrieveCurrentSolutions);
        }

        private void tsbRefreshSolutions_Click(object sender, EventArgs e)
        {
            ExecuteMethod(RetrieveCurrentSolutions);
        }
    }

    struct SolutionComponent
    {
        //public const string SolutionManifests = "solutionManifest";
        public const string Upgrades = "upgradeHandler";
        public const string ValidateHandlers = "validateHandler";
        //public const string Nodes = "node";

        public const string Entities = "entity";
        public const string Optionsets = "optionSet";
        //public const string Ribbons = "ribbon";
        public const string Dashboards = "dashboard";
        public const string Reports = "report";
        
        public const string FieldSecurityProfiles = "FieldSecurityProfile";
        public const string SecurityRoles = "securityrole";
        public const string Templates = "template";
        public const string RoutingRules = "routingrule";
        public const string Slas = "Sla";
        public const string Webresources = "webResource";
        public const string CustomControls = "customControl";

        public const string Workflows = "workflow";
        public const string Dialogs = "dialog";
        public const string SolutionPluginAssemblies = "PluginAssembly";
        public const string SdkMessageProcessingSteps = "SdkMessageProcessingStep";
    }

    struct ComponentResult
    {
        public const string Warnings = "warning";
        public const string Errors = "error";
        public const string Failures = "failure";
        public const string Success = "success";
    }

    struct SolutionAttribute
    {
        public const string Name = "name";
        public const string LocalizedName = "LocalizedName";
        public const string LocalizedNames = "LocalizedNames";
        public const string Id = "id";
        public const string Result = "result";
        public const string Description = "description";
        public const string ErrorCode = "errorcode";
        public const string ErrorText = "errortext"; 
    }

    struct SolutionElement
    {
        public const string SolutionManifests = "solutionManifests";
        public const string SolutionManifest = "solutionManifest";
        public const string UpgradeSolutionPackageInformation = "upgradeSolutionPackageInformation";
        public const string LocalizedName = "LocalizedName";
        public const string LocalizedNames = "LocalizedNames";
        public const string Version = "Version";
        public const string Managed = "Managed";
        public const string CurrentVersion = "currentVersion";
        public const string FileVersion = "fileVersion";
        public const string Publisher = "Publisher";
        public const string UniqueName = "UniqueName";
    }
}
