﻿/* Copyright (c) Citrix Systems Inc. 
 * All rights reserved. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using log4net;
using XenAdmin.Controls;
using XenAdmin.Diagnostics.Problems;
using XenAdmin.Dialogs;
using XenAdmin.Wizards.PatchingWizard.PlanActions;
using XenAPI;
using XenAdmin.Actions;
using XenAdmin.Core;

namespace XenAdmin.Wizards.PatchingWizard
{
    public partial class PatchingWizard_PatchingPage : XenTabPage
    {
        protected static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public Dictionary<string, LivePatchCode> LivePatchCodesByHost
        {
            get;
            set;
        }

        private AsyncAction actionManualMode = null;
        private bool _thisPageHasBeenCompleted = false;
        private BackgroundWorker actionsWorker = null;

        public PatchingWizard_PatchingPage()
        {
            InitializeComponent();
        }

        #region Accessors
        public List<Host> SelectedMasters { private get; set; }
        public List<Host> SelectedServers { private get; set; }
        public List<Pool> SelectedPools { private get; set; }
        public UpdateType SelectedUpdateType { private get; set; }
        public Pool_patch Patch { private get; set; }
        public Pool_update PoolUpdate { private get; set; }

        public string ManualTextInstructions { private get; set; }
        public bool IsAutomaticMode { private get; set; }
        public bool RemoveUpdateFile { private get; set; }
        public string SelectedNewPatch { private get; set; }
        public List<Problem> ProblemsResolvedPreCheck { private get; set; }
        public Dictionary<Host, VDI> SuppPackVdis { private get; set; }
        #endregion

        public override string Text
        {
            get
            {
                return Messages.PATCHINGWIZARD_PATCHINGPAGE_TEXT;
            }
        }

        public override string PageTitle
        {
            get
            {
                return Messages.PATCHINGWIZARD_PATCHINGPAGE_TITLE;
            }
        }

        public override string HelpID
        {
            get { return "InstallUpdate"; }
        }

        public override bool EnablePrevious()
        {
            return false;
        }

        private bool _nextEnabled;
        public override bool EnableNext()
        {
            return _nextEnabled;
        }

        private bool _cancelEnabled;
        public override bool EnableCancel()
        {
            return _cancelEnabled;
        }

        public override void PageLoaded(PageLoadedDirection direction)
        {
            base.PageLoaded(direction);

            if (_thisPageHasBeenCompleted)
            {
                actionManualMode = null;
                actionsWorker = null;
                return;
            }

            if (!IsAutomaticMode)
            {
                textBoxLog.Text = ManualTextInstructions;
                
                List<AsyncAction> actions = new List<AsyncAction>();

                if (SelectedUpdateType == UpdateType.ISO && PoolUpdate != null)
                {
                    //Ely or greater: iso update format
                    actionManualMode = new ApplyUpdateAction(new List<Pool_update>() { PoolUpdate }, SelectedServers);
                }
                else
                {
                    //legacy mode
                    if (SelectedUpdateType != UpdateType.ISO)
                        actionManualMode = new ApplyPatchAction(new List<Pool_patch> { Patch }, SelectedServers);
                    else
                        actionManualMode = new InstallSupplementalPackAction(SuppPackVdis, false);
                }

                actions.Add(actionManualMode);
                if (RemoveUpdateFile && SelectedUpdateType != UpdateType.ISO)
                {
                    foreach (Pool pool in SelectedPools)
                    {
                        actions.Add(new PoolPatchCleanAction(pool, Patch, false));
                    }
                }

                using (var multipleAction = new MultipleAction(Connection, "", "", "", actions, true, true, true))
                {
                    multipleAction.Changed += action_Changed;
                    multipleAction.Completed += action_Completed;
                    multipleAction.RunAsync();
                }
                return;
            }

            _nextEnabled = false;
            OnPageUpdated();

            List<PlanAction> planActions = new List<PlanAction>();

            foreach (Pool pool in SelectedPools)
            {
                var poolHosts = new List<Host>(pool.Connection.Cache.Hosts);
                Host master = SelectedServers.Find(host => host.IsMaster() && poolHosts.Contains(host));

                //For Ely or greater: ISO updates only
                if (SelectedUpdateType == UpdateType.ISO && Helpers.ElyOrGreater(pool.Connection)) //updates for Ely (or higher) are always ISO
                {
                    var poolUpdates = new List<Pool_update>(pool.Connection.Cache.Pool_updates);
                    var poolUpdate = poolUpdates.FirstOrDefault(u => u != null && string.Equals(u.uuid, PoolUpdate.uuid, StringComparison.OrdinalIgnoreCase));

                    //master first
                    if (master != null && !poolUpdate.AppliedOn(master))
                    {
                        planActions.AddRange(CompilePoolUpdateActionList(master, poolUpdate));
                    }

                    //other hosts
                    foreach (var host in SelectedServers.Where(s => poolHosts.Contains(s) && !s.IsMaster() && !poolUpdate.AppliedOn(s)).ToList())
                    {
                        planActions.AddRange(CompilePoolUpdateActionList(host, poolUpdate));
                    }
                }
                // Legacy (pre-Ely) case: either ISO for supplemental packs (Pool_patch == null) or patch (Pool_patch != null)
                else
                {
                    Pool_patch poolPatch = null;

                    if (SelectedUpdateType != UpdateType.ISO)
                    {
                        List<Pool_patch> poolPatches = new List<Pool_patch>(pool.Connection.Cache.Pool_patches);

                        poolPatch = poolPatches.Find(delegate(Pool_patch otherPatch) 
                                                        {
                                                            if (Patch != null)
                                                                return string.Equals(otherPatch.uuid, Patch.uuid, StringComparison.OrdinalIgnoreCase);
                                                            return false;
                                                        });
                    }

                    //master first
                    if (master != null && (poolPatch == null || poolPatch.AppliedOn(master) == DateTime.MaxValue))
                        planActions.AddRange(CompileActionList(master, poolPatch));

                    foreach (Host server in SelectedServers)
                    {
                        if (poolHosts.Contains(server))
                        {
                            if (!server.IsMaster())
                            {
                                // check patch isn't already applied here
                                if (poolPatch == null || poolPatch.AppliedOn(server) == DateTime.MaxValue)
                                    planActions.AddRange(CompileActionList(server, poolPatch));
                            }
                        }
                    }

                    if (RemoveUpdateFile)
                    {
                        planActions.Add(new RemoveUpdateFile(pool, poolPatch));
                    }
                }
            } //end pool in foreach

            planActions.Add(new UnwindProblemsAction(ProblemsResolvedPreCheck));

            actionsWorker = new BackgroundWorker();
            actionsWorker.DoWork += new DoWorkEventHandler(PatchingWizardAutomaticPatchWork);
            actionsWorker.WorkerReportsProgress = true;
            actionsWorker.ProgressChanged += new ProgressChangedEventHandler(actionsWorker_ProgressChanged);
            actionsWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(actionsWorker_RunWorkerCompleted);
            actionsWorker.WorkerSupportsCancellation = true;
            actionsWorker.RunWorkerAsync(planActions);
        }
        
        #region manual_mode

        private void action_Completed(ActionBase sender)
        {
            _nextEnabled = true;
            _cancelEnabled = false;
            
            try
            {
                if (sender.Exception != null)
                {
                    Program.Invoke(Program.MainWindow, () => FinishedWithErrors(new Exception(sender.Title, sender.Exception)));
                }
                else
                    Program.Invoke(Program.MainWindow, FinishedSuccessfully);
            }
            catch (Exception except)
            {
                Program.Invoke(Program.MainWindow, () => FinishedWithErrors(except));
            }
            Program.Invoke(Program.MainWindow, OnPageUpdated);
            _thisPageHasBeenCompleted = true;
        }

        private void action_Changed(ActionBase sender)
        {
            AsyncAction action = (AsyncAction)sender;
            Program.Invoke(Program.MainWindow, delegate { progressBar.Value = action.PercentComplete; });
        }

        #endregion

        #region automatic_mode

        private void actionsWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (!actionsWorker.CancellationPending)
            {
                PlanAction action = (PlanAction)e.UserState;
                if (e.ProgressPercentage == 0)
                    textBoxLog.Text += action;
                else
                {
                    textBoxLog.Text += string.Format("{0}\r\n", Messages.DONE);
                    progressBar.Value += e.ProgressPercentage;
                }
            }
        }      

        private void PatchingWizardAutomaticPatchWork(object obj, DoWorkEventArgs doWorkEventArgs)
        {
            List<PlanAction> actionList = (List<PlanAction>)doWorkEventArgs.Argument;
            foreach (PlanAction action in actionList)
            {
                try
                {
                    if (actionsWorker.CancellationPending)
                    {
                        doWorkEventArgs.Cancel = true;
                        return;
                    }
                    this.actionsWorker.ReportProgress(0, action);
                    action.Run();
                    Thread.Sleep(1000);

                    this.actionsWorker.ReportProgress((int)((1.0 / (double)actionList.Count) * 100), action);
                    
                }
                catch (Exception e)
                {

                    log.Error("Failed to carry out plan.", e);
                    log.Debug(actionList);
                    doWorkEventArgs.Result = new Exception(action.Title, e);
                    break;
                }
            }
        }

        private void actionsWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (!e.Cancelled)
            {
                Exception exception = e.Result as Exception;
                if (exception != null)
                {
                    FinishedWithErrors(exception);

                }
                else
                {
                    FinishedSuccessfully();
                }

                progressBar.Value = 100;
                _cancelEnabled = false;
                _nextEnabled = true;
            }
            OnPageUpdated();
            _thisPageHasBeenCompleted = true;
        }

        private List<PlanAction> CompileActionList(Host host, Pool_patch patch)
        {
            if (SelectedUpdateType == UpdateType.ISO)
                return CompileSuppPackActionList(host);

            List<PlanAction> actions = new List<PlanAction>();

            if (patch == null)
                return actions;

            List<XenRef<VM>> runningVMs = RunningVMs(host, patch);

            actions.Add(new ApplyPatchPlanAction(host, patch));

            if (patch.after_apply_guidance.Contains(after_apply_guidance.restartHost) 
                && !(LivePatchCodesByHost !=null && LivePatchCodesByHost.ContainsKey(host.uuid) && LivePatchCodesByHost[host.uuid] == LivePatchCode.PATCH_PRECHECK_LIVEPATCH_COMPLETE))
            {
                actions.Add(new EvacuateHostPlanAction(host));
                actions.Add(new RebootHostPlanAction(host));
                actions.Add(new BringBabiesBackAction(runningVMs, host, false));
            }

            if (patch.after_apply_guidance.Contains(after_apply_guidance.restartXAPI))
            {
                actions.Add(new RestartAgentPlanAction(host));
            }

            if (patch.after_apply_guidance.Contains(after_apply_guidance.restartHVM))
            {
                actions.Add(new RebootVMsPlanAction(host, RunningHvmVMs(host)));
            }

            if (patch.after_apply_guidance.Contains(after_apply_guidance.restartPV))
            {
                actions.Add(new RebootVMsPlanAction(host, RunningPvVMs(host)));
            }

            return actions;
        }

        private static List<XenRef<VM>> RunningVMs(Host host, Pool_patch patch)
        {
            List<XenRef<VM>> vms = new List<XenRef<VM>>();
            foreach (VM vm in patch.Connection.ResolveAll(host.resident_VMs))
            {
                if (!vm.is_a_real_vm)
                    continue;

                vms.Add(new XenRef<VM>(vm.opaque_ref));
            }
            return vms;
        }

        private static List<XenRef<VM>> RunningHvmVMs(Host host)
        {
            List<XenRef<VM>> vms = new List<XenRef<VM>>();
            foreach (VM vm in host.Connection.ResolveAll(host.resident_VMs))
            {
                if (!vm.IsHVM || !vm.is_a_real_vm)
                    continue;
                vms.Add(new XenRef<VM>(vm.opaque_ref));
            }
            return vms;
        }

        private static List<XenRef<VM>> RunningPvVMs(Host host)
        {
            List<XenRef<VM>> vms = new List<XenRef<VM>>();
            foreach (VM vm in host.Connection.ResolveAll(host.resident_VMs))
            {
                if (vm.IsHVM || !vm.is_a_real_vm)
                    continue;
                vms.Add(new XenRef<VM>(vm.opaque_ref));
            }
            return vms;
        }

        private static List<XenRef<VM>> RunningVMs(Host host)
        {
            List<XenRef<VM>> vms = new List<XenRef<VM>>();
            foreach (VM vm in host.Connection.ResolveAll(host.resident_VMs))
            {
                if (!vm.is_a_real_vm)
                    continue;

                vms.Add(new XenRef<VM>(vm.opaque_ref));
            }
            return vms;
        }

        private List<PlanAction> CompileSuppPackActionList(Host host)
        {
            List<PlanAction> actions = new List<PlanAction>();
            
            if (SelectedUpdateType != UpdateType.ISO || SuppPackVdis == null || !SuppPackVdis.ContainsKey(host))
                return actions;
            
            List<XenRef<VM>> runningVMs = RunningVMs(host);

            actions.Add(new InstallSupplementalPackPlanAction(host, SuppPackVdis[host]));

            // after_apply_guidance is restartHost
            actions.Add(new EvacuateHostPlanAction(host));
            actions.Add(new RebootHostPlanAction(host));
            actions.Add(new BringBabiesBackAction(runningVMs, host, false));

            return actions;
        }

        private List<PlanAction> CompilePoolUpdateActionList(Host host, Pool_update poolUpdate)
        {
            List<PlanAction> actions = new List<PlanAction>();

            if (SelectedUpdateType != UpdateType.ISO || poolUpdate == null)
                return actions;

            List<XenRef<VM>> runningVMs = RunningVMs(host);

            actions.Add(new ApplyPoolUpdatePlanAction(host, poolUpdate));

            if (poolUpdate.after_apply_guidance.Contains(update_after_apply_guidance.restartHost)
                && !(LivePatchCodesByHost != null && LivePatchCodesByHost.ContainsKey(host.uuid) && LivePatchCodesByHost[host.uuid] == LivePatchCode.PATCH_PRECHECK_LIVEPATCH_COMPLETE))
            {
                actions.Add(new EvacuateHostPlanAction(host));
                actions.Add(new RebootHostPlanAction(host));
                actions.Add(new BringBabiesBackAction(runningVMs, host, false));
            }

            if (poolUpdate.after_apply_guidance.Contains(update_after_apply_guidance.restartXAPI))
            {
                actions.Add(new RestartAgentPlanAction(host));
            }

            if (poolUpdate.after_apply_guidance.Contains(update_after_apply_guidance.restartHVM))
            {
                actions.Add(new RebootVMsPlanAction(host, RunningHvmVMs(host)));
            }

            if (poolUpdate.after_apply_guidance.Contains(update_after_apply_guidance.restartPV))
            {
                actions.Add(new RebootVMsPlanAction(host, RunningPvVMs(host)));
            }
            
            return actions;
        }


        #endregion

        private void FinishedWithErrors(Exception exception)
        {
            labelTitle.Text = string.Format(Messages.UPDATE_WAS_NOT_COMPLETED, GetUpdateName());
            
            string errorMessage = null;

            if (exception != null && exception.InnerException != null && exception.InnerException is Failure)
            {
                var innerEx = exception.InnerException as Failure;
                errorMessage = innerEx.Message;

                if (innerEx.ErrorDescription != null && innerEx.ErrorDescription.Count > 0)
                    log.Error(string.Concat(innerEx.ErrorDescription.ToArray()));
            }
            
            labelError.Text = errorMessage ?? string.Format(Messages.PATCHING_WIZARD_ERROR, exception.Message);

            log.ErrorFormat("Error message displayed: {0}", labelError.Text);

            pictureBox1.Image = SystemIcons.Error.ToBitmap();
            if (exception.InnerException is SupplementalPackInstallFailedException)
            {
                errorLinkLabel.Visible = true;
                errorLinkLabel.Tag = exception.InnerException;
            }
            panel1.Visible = true;
        }

        /// <summary>
        /// Live patching is attempted for a host if the LivePatchCodesByHost contains the LIVEPATCH_COMPLETE value for that host
        /// </summary>
        /// <param name="host"></param>
        /// <returns></returns>
        private bool LivePatchingAttemptedForHost(Host host)
        {
            return LivePatchCodesByHost != null && LivePatchCodesByHost.ContainsKey(host.uuid) &&
                   LivePatchCodesByHost[host.uuid] == LivePatchCode.PATCH_PRECHECK_LIVEPATCH_COMPLETE;

        }

        /// <summary>
        /// Live patching has failed for a host if that host requires a reboot for this patch, and we expected to live patch
        /// </summary>
        /// <param name="host"></param>
        /// <returns></returns>
        private bool LivePatchingFailedForHost(Host host)
        {
            if (!host.patches_requiring_reboot.Any())
            {
                return false;
            }

            foreach (var patchRef in host.patches_requiring_reboot)
            {
                var poolPatch = host.Connection.Resolve(patchRef);
                if (poolPatch.uuid.Equals(Patch.uuid))
                {
                    // This patch failed
                    return true;
                }
            }

            return false;
        }

        private void FinishedSuccessfully()
        {
            labelTitle.Text = string.Format(Messages.UPDATE_WAS_SUCCESSFULLY_INSTALLED, GetUpdateName());
            pictureBox1.Image = null;
            labelError.Text = Messages.CLOSE_WIZARD_CLICK_FINISH;

            // Live patching failed is per-host
            var livePatchingFailedHosts = new List<Host>();

            foreach (var host in SelectedMasters)
            {
                if (LivePatchingAttemptedForHost(host) && LivePatchingFailedForHost(host))
                {
                    livePatchingFailedHosts.Add(host);
                }
            }

            if (livePatchingFailedHosts.Count == 0)
            {
                return;
            }

            LivePatchingFailed(livePatchingFailedHosts);
        }

        private void LivePatchingFailed(IList<Host> hosts)
        {
            string dialogMessage;
            if (hosts.Count == 1)
            {
                dialogMessage = string.Format(Messages.LIVE_PATCHING_FAILED_ONE_HOST, hosts[0]);
            }
            else
            {
                dialogMessage = string.Format(Messages.LIVE_PATCHING_FAILED_MULTI_HOST,
                    string.Join(Messages.LIST_SEPARATOR, hosts.Select(s => s.ToString().SurroundWith('\''))));
            }

            using (var dlg = new ThreeButtonDialog(
                new ThreeButtonDialog.Details(SystemIcons.Warning,
                    dialogMessage, Messages.XENCENTER),
                ThreeButtonDialog.ButtonOK))
            {
                dlg.ShowDialog(Program.MainWindow);
            }
        }

        private string GetUpdateName()
        {
            if (Patch != null)
                return Patch.Name;
            try
            {
                return new FileInfo(SelectedNewPatch).Name;
            }
            catch (Exception)
            {
                return SelectedNewPatch;
            }
        }

        private void errorLinkLabel_LinkClicked(object sender, System.Windows.Forms.LinkLabelLinkClickedEventArgs e)
        {
            if (errorLinkLabel.Tag is Exception)
                ShowMoreInfo(errorLinkLabel.Tag as Exception);
        }

        private void ShowMoreInfo(Exception exception)
        {
            if (!(exception is SupplementalPackInstallFailedException)) 
                return;
            var msg = string.Format(Messages.SUPP_PACK_INSTALL_FAILED_MORE_INFO, exception.Message);
            using (var dlg = new ThreeButtonDialog(new ThreeButtonDialog.Details(SystemIcons.Error, msg)))
            {
                dlg.ShowDialog(this);
            }
        }
    }
}
