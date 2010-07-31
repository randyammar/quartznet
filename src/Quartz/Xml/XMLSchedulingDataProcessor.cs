/* 
 * Copyright 2001-2010 Terracotta, Inc. 
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not 
 * use this file except in compliance with the License. You may obtain a copy 
 * of the License at 
 * 
 *   http://www.apache.org/licenses/LICENSE-2.0 
 *   
 * Unless required by applicable law or agreed to in writing, software 
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT 
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the 
 * License for the specific language governing permissions and limitations 
 * under the License.
 * 
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using Common.Logging;
using Quartz.Spi;
using Quartz.Util;
using Quartz.Xml.JobSchedulingData20;

namespace Quartz.Xml
{
    /// <summary> 
    /// Parses an XML file that declares Jobs and their schedules (Triggers).
    /// </summary>
    /// <remarks>
    /// <p>
    /// The xml document must conform to the format defined in
    /// "job_scheduling_data_2_0.xsd"
    /// </p>
    /// 
    /// <p>
    /// After creating an instance of this class, you should call one of the <see cref="ProcessFile()" />
    /// functions, after which you may call the ScheduledJobs()
    /// function to get a handle to the defined Jobs and Triggers, which can then be
    /// scheduled with the <see cref="IScheduler" />. Alternatively, you could call
    /// the <see cref="ProcessFileAndScheduleJobs(Quartz.IScheduler)" /> function to do all of this
    /// in one step.
    /// </p>
    /// 
    /// <p>
    /// The same instance can be used again and again, with the list of defined Jobs
    /// being cleared each time you call a <see cref="ProcessFile()" /> method,
    /// however a single instance is not thread-safe.
    /// </p>
    /// </remarks>
    /// <author><a href="mailto:bonhamcm@thirdeyeconsulting.com">Chris Bonham</a></author>
    /// <author>James House</author>
    /// <author>Marko Lahma (.NET)</author>
    public class XMLSchedulingDataProcessor
    {
        private readonly ILog log;
        private readonly bool validateXml;
        private readonly bool validateSchema;

        public const string PropertyQuartzSystemIdDir = "quartz.system.id.dir";
        public const string QuartzXmlFileName = "quartz_jobs.xml";
        public const string QuartzSchema = "http://quartznet.sourceforge.net/xml/job_scheduling_data_2_0.xsd";
        public const string QuartzXsdResourceName = "Quartz.Quartz.Xml.job_scheduling_data_2_0.xsd";

        protected const string ThreadLocalKeyScheduler = "quartz_scheduler";

        /// <summary> 
        /// XML Schema dateTime datatype format.
        /// <p>
        /// See <a href="http://www.w3.org/TR/2001/REC-xmlschema-2-20010502/#dateTime">
        /// http://www.w3.org/TR/2001/REC-xmlschema-2-20010502/#dateTime</a>
        /// </p>
        /// </summary>
        protected const string XsdDateFormat = "yyyy-MM-dd'T'hh:mm:ss";

        // pre-processing commands
        protected IList<String> jobGroupsToDelete = new List<String>();
        protected IList<String> triggerGroupsToDelete = new List<String>();
        protected IList<Key> jobsToDelete = new List<Key>();
        protected IList<Key> triggersToDelete = new List<Key>();

        // scheduling commands
        protected IList<JobDetail> loadedJobs = new List<JobDetail>();
        protected IList<Trigger> loadedTriggers = new List<Trigger>();

        // directives
        private bool overWriteExistingData = true;
        private bool ignoreDuplicates = false;

        protected IList<Exception> validationExceptions = new List<Exception>();


        protected internal ITypeLoadHelper typeLoadHelper;
        protected IList<String> jobGroupsToNeverDelete = new List<String>();
        protected IList<String> triggerGroupsToNeverDelete = new List<String>();

        /// <summary>
        /// Constructor for XMLSchedulingDataProcessor.
        /// </summary>
        public XMLSchedulingDataProcessor(ITypeLoadHelper typeLoadHelper)
        {
            log = LogManager.GetLogger(GetType());
            this.typeLoadHelper = typeLoadHelper;
        }

        /// <summary>
        /// Whether the existing scheduling data (with same identifiers) will be 
        /// overwritten. 
        /// </summary>
        /// <remarks>
        /// If false, and <code>IgnoreDuplicates</code> is not false, and jobs or 
        /// triggers with the same names already exist as those in the file, an 
        /// error will occur.
        /// </remarks> 
        /// <seealso cref="IgnoreDuplicates" />
        public bool OverWriteExistingData
        {
            get { return overWriteExistingData; }
            set { overWriteExistingData = value; }
        }

        /// <summary>
        /// If true (and <code>OverWriteExistingData</code> is false) then any 
        /// job/triggers encountered in this file that have names that already exist 
        /// in the scheduler will be ignored, and no error will be produced.
        /// </summary>
        /// <seealso cref="OverWriteExistingData"/>
        public bool IgnoreDuplicates
        {
            get { return ignoreDuplicates; }
            set { ignoreDuplicates = value; }
        }

        /// <summary>
        /// Gets the log.
        /// </summary>
        /// <value>The log.</value>
        protected internal ILog Log
        {
            get { return log; }
        }

        /// <summary> 
        /// Process the xml file in the default location (a file named
        /// "quartz_jobs.xml" in the current working directory).
        /// </summary>
        public virtual void ProcessFile()
        {
            ProcessFile(QuartzXmlFileName);
        }

        /// <summary>
        /// Process the xml file named <see param="fileName" />.
        /// </summary>
        /// <param name="fileName">meta data file name.</param>
        public virtual void ProcessFile(string fileName)
        {
            ProcessFile(fileName, fileName);
        }

        /// <summary>
        /// Process the xmlfile named <see param="fileName" /> with the given system
        /// ID.
        /// </summary>
        /// <param name="fileName">Name of the file.</param>
        /// <param name="systemId">The system id.</param>
        public virtual void ProcessFile(string fileName, string systemId)
        {
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                                   "Parsing XML file: {0} with systemId: {1} validating: {2} validating schema: {3}",
                                   fileName, systemId, validateXml, validateSchema));
            using (StreamReader sr = new StreamReader(fileName))
            {
                ProcessInternal(sr.ReadToEnd());
            }
        }

        /// <summary>
        /// Process the xmlfile named <see param="fileName" /> with the given system
        /// ID.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="systemId">The system id.</param>
        public virtual void ProcessStream(Stream stream, string systemId)
        {
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                                   "Parsing XML from stream with systemId: {0} validating: {1} validating schema: {2}",
                                   systemId, validateXml, validateSchema));
            using (StreamReader sr = new StreamReader(stream))
            {
                ProcessInternal(sr.ReadToEnd());
            }
        }

        protected virtual void PrepForProcessing()
        {
            ClearValidationExceptions();

            OverWriteExistingData = true;
            IgnoreDuplicates = false;

            jobGroupsToDelete.Clear();
            jobsToDelete.Clear();
            triggerGroupsToDelete.Clear();
            triggersToDelete.Clear();

            loadedJobs.Clear();
            loadedTriggers.Clear();
        }

        protected virtual void ProcessInternal(string xml)
        {
            PrepForProcessing();

            ValidateXml(xml);

            // deserialize as object model
            XmlSerializer xs = new XmlSerializer(typeof (QuartzXmlConfiguration20));
            QuartzXmlConfiguration20 data = (QuartzXmlConfiguration20) xs.Deserialize(new StringReader(xml));

            if (data == null)
            {
                throw new SchedulerConfigException("Job definition data from XML was null after deserialization");
            }

            //
            // Extract pre-processing commands
            //
            if (data.preprocessingcommands != null)
            {
                foreach (preprocessingcommandsType command in data.preprocessingcommands)
                {
                    if (command.deletejobsingroup != null)
                    {
                        foreach (string s in command.deletejobsingroup)
                        {
                            string deleteJobGroup = s.NullSafeTrim();
                            if (!String.IsNullOrEmpty(deleteJobGroup))
                            {
                                jobGroupsToDelete.Add(deleteJobGroup);
                            }
                        }

                    }
                    if (command.deletetriggersingroup != null)
                    {
                        foreach (string s in command.deletetriggersingroup)
                        {
                            string deleteTriggerGroup = s.NullSafeTrim();
                            if (!String.IsNullOrEmpty(deleteTriggerGroup))
                            {
                                triggerGroupsToDelete.Add(deleteTriggerGroup);
                            }
                        }
                    }
                    if (command.deletejob != null)
                    {
                        foreach (preprocessingcommandsTypeDeletejob s in command.deletejob)
                        {
                            String name = s.name.TrimEmptyToNull();
                            String group = s.group.TrimEmptyToNull();

                            if (name == null)
                            {
                                throw new SchedulerConfigException("Encountered a 'delete-job' command without a name specified.");
                            }
                            jobsToDelete.Add(new Key(name, group));
                        }
                    }
                    if (command.deletetrigger != null)
                    {
                        foreach (preprocessingcommandsTypeDeletetrigger s in command.deletetrigger)
                        {
                            String name = s.name.TrimEmptyToNull();
                            String group = s.group.TrimEmptyToNull();

                            if (name == null)
                            {
                                throw new SchedulerConfigException("Encountered a 'delete-trigger' command without a name specified.");
                            }
                            triggersToDelete.Add(new Key(name, group));
                        }
                    }
                }
            }

            log.Debug("Found " + jobGroupsToDelete.Count + " delete job group commands.");
            log.Debug("Found " + triggerGroupsToDelete.Count + " delete trigger group commands.");
            log.Debug("Found " + jobsToDelete.Count + " delete job commands.");
            log.Debug("Found " + triggersToDelete.Count + " delete trigger commands.");

            //
            // Extract directives
            //


            if (data.processingdirectives != null && data.processingdirectives.Length > 0)
            {
                bool overWrite = data.processingdirectives[0].overwriteexistingdata;
                log.Debug("Directive 'overwrite-existing-data' specified as: " + overWrite);
                OverWriteExistingData = overWrite;
            }
            else
            {
                log.Debug("Directive 'ignore-duplicates' not specified, defaulting to " + OverWriteExistingData);
            }

            if (data.processingdirectives != null && data.processingdirectives.Length > 0)
            {
                bool ignoreduplicates = data.processingdirectives[0].ignoreduplicates;
                log.Debug("Directive 'ignore-duplicates' specified as: " + ignoreduplicates);
                IgnoreDuplicates = ignoreduplicates;
            }
            else
            {
                log.Debug("Directive 'overwrite-existing-data' not specified, defaulting to " + IgnoreDuplicates);
            }

            //
            // Extract Job definitions...
            //

            List<jobdetailType> jobNodes = new List<jobdetailType>();
            if (data.schedule != null && data.schedule.Length > 0 && data.schedule[0].job != null)
            {
                jobNodes.AddRange(data.schedule[0].job);
            }
            
            log.Debug("Found " + jobNodes.Count + " job definitions.");

            foreach (jobdetailType jobDetailType in jobNodes)
            {
                String jobName = jobDetailType.name.TrimEmptyToNull();
                String jobGroup = jobDetailType.group.TrimEmptyToNull();
                String jobDescription = jobDetailType.description.TrimEmptyToNull();
                String jobTypeName = jobDetailType.jobtype.TrimEmptyToNull();
                bool jobVolatility = jobDetailType.volatility;
                bool jobDurability = jobDetailType.durability;
                bool jobRecoveryRequested = jobDetailType.recover;

                Type jobClass = typeLoadHelper.LoadType(jobTypeName);

                JobDetail jobDetail = new JobDetail(jobName, jobGroup,
                        jobClass, jobVolatility, jobDurability,
                        jobRecoveryRequested);
                jobDetail.Description = jobDescription;

                if (jobDetailType.jobdatamap != null && jobDetailType.jobdatamap.entry != null)
                {
                    foreach (entryType entry in jobDetailType.jobdatamap.entry)
                    {
                        String key = entry.key.TrimEmptyToNull();
                        String value = entry.value.TrimEmptyToNull();
                        jobDetail.JobDataMap.Add(key, value);
                    }
                }

                if (log.IsDebugEnabled)
                {
                    log.Debug("Parsed job definition: " + jobDetail);
                }

                AddJobToSchedule(jobDetail);
            }

            //
            // Extract Trigger definitions...
            //

            List<triggerType> triggerEntries = new List<triggerType>();
            if (data.schedule != null && data.schedule.Length > 0 && data.schedule[0].trigger != null)
            {
                triggerEntries.AddRange(data.schedule[0].trigger);
            }

            log.Debug("Found " + triggerEntries.Count + " trigger definitions.");

            foreach (triggerType triggerNode in triggerEntries)
            {
                String triggerName = triggerNode.Item.name.TrimEmptyToNull();
                String triggerGroup = triggerNode.Item.group.TrimEmptyToNull();
                String triggerDescription = triggerNode.Item.description.TrimEmptyToNull();
                String triggerMisfireInstructionConst;
                String triggerCalendarRef = triggerNode.Item.calendarname.TrimEmptyToNull();
                String triggerJobName = triggerNode.Item.jobname.TrimEmptyToNull();
                String triggerJobGroup = triggerNode.Item.jobgroup.TrimEmptyToNull();
                bool triggerVolatility = triggerNode.Item.volatilitySpecified ? triggerNode.Item.volatility : true;

                DateTime triggerStartTime = triggerNode.Item.starttime;
                DateTime? triggerEndTime = triggerNode.Item.endtimeSpecified ? triggerNode.Item.endtime : (DateTime?) null;

                Trigger trigger = null;

                if (triggerNode.Item is simpleTriggerType)
                {
                    simpleTriggerType simpleTrigger = (simpleTriggerType) triggerNode.Item;
                    triggerMisfireInstructionConst = simpleTrigger.misfireinstruction;
                    String repeatCountString = simpleTrigger.repeatcount.TrimEmptyToNull();
                    String repeatIntervalString = simpleTrigger.repeatinterval.TrimEmptyToNull();

                    int repeatCount = ParseSimpleTriggerRepeatCount(repeatCountString);
                    TimeSpan repeatInterval = repeatIntervalString == null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Convert.ToInt64(repeatIntervalString));

                    trigger = new SimpleTrigger(triggerName, triggerGroup,
                            triggerJobName, triggerJobGroup,
                            triggerStartTime, triggerEndTime,
                            repeatCount, repeatInterval);
                }
                else if (triggerNode.Item is cronTriggerType)
                {
                    cronTriggerType cronTrigger = (cronTriggerType) triggerNode.Item;
                    triggerMisfireInstructionConst = cronTrigger.misfireinstruction;
                    String cronExpression = cronTrigger.cronexpression.TrimEmptyToNull();
                    String timezoneString = cronTrigger.timezone.TrimEmptyToNull();

                    if (!String.IsNullOrEmpty(timezoneString))
                    {
#if NET_35
                        trigger = new CronTrigger(triggerName, triggerGroup,
                                                  triggerJobName, triggerJobGroup,
                                                  triggerStartTime, triggerEndTime,
                                                  cronExpression, TimeZoneInfo.FindSystemTimeZoneById(timezoneString));
#else
	                        throw new ArgumentException(
	                            "Specifying time zone for cron trigger is only supported in .NET 3.5 builds and later");
#endif
                    }
                    else
                    {
                        trigger = new CronTrigger(triggerName, triggerGroup,
                                                  triggerJobName, triggerJobGroup,
                                                  triggerStartTime, triggerEndTime,
                                                  cronExpression);
                    }

                }
                else
                {
                    throw new SchedulerConfigException("Unknown trigger type in XML configuration");
                }

                trigger.Volatile = triggerVolatility;
                trigger.Description = triggerDescription;
                trigger.CalendarName = triggerCalendarRef;

                if (!String.IsNullOrEmpty(triggerMisfireInstructionConst))
                {
                    trigger.MisfireInstruction = ReadMisfireInstructionFromString(triggerMisfireInstructionConst);
                }

                if (triggerNode.Item.jobdatamap != null && triggerNode.Item.jobdatamap.entry != null)
                {
                    foreach (entryType entry in triggerNode.Item.jobdatamap.entry)
                    {
                        String key = entry.key.TrimEmptyToNull();
                        String value = entry.value.TrimEmptyToNull();
                        trigger.JobDataMap.Add(key, value);
                    }
                }

                if (log.IsDebugEnabled)
                {
                    log.Debug("Parsed trigger definition: " + trigger);
                }

                AddTriggerToSchedule(trigger);
            }

        }


        protected virtual void AddJobToSchedule(JobDetail job)
        {
            loadedJobs.Add(job);
        }

        protected virtual void AddTriggerToSchedule(Trigger trigger)
        {
            loadedTriggers.Add(trigger);
        }


        protected virtual int ParseSimpleTriggerRepeatCount(string repeatcount)
        {
            int value;
            if (repeatcount == "RepeatIndefinitely")
            {
                value = SimpleTrigger.RepeatIndefinitely;
            }
            else
            {
                value = Convert.ToInt32(repeatcount, CultureInfo.InvariantCulture);
            }

            return value;
        }

        protected virtual int ReadMisfireInstructionFromString(string misfireinstruction)
        {
            Constants c = new Constants(typeof (MisfireInstruction), typeof (MisfireInstruction.CronTrigger),
                                        typeof (MisfireInstruction.SimpleTrigger));
            return c.AsNumber(misfireinstruction);
        }

        private void ValidateXml(string xml)
        {
            // stream to validate
            using (StringReader stringReader = new StringReader(xml))
            {
                XmlTextReader xmlr = new XmlTextReader(stringReader);
                XmlValidatingReader xmlvread = new XmlValidatingReader(xmlr);

                // Set the validation event handler
                xmlvread.ValidationEventHandler += XmlValidationCallBack;

                // Read XML data
                while (xmlvread.Read())
                {
                }

                //Close the reader.
                xmlvread.Close();
            }
    
        }

        private void XmlValidationCallBack(object sender, ValidationEventArgs e)
        {
            validationExceptions.Add(e.Exception);
        }


        /// <summary> 
        /// Process the xml file in the default location, and schedule all of the
        /// jobs defined within it.
        /// </summary>
        public virtual void ProcessFileAndScheduleJobs(IScheduler sched)
        {
            ProcessFileAndScheduleJobs(QuartzXmlFileName, sched);
        }

        /// <summary>
        /// Process the xml file in the given location, and schedule all of the
        /// jobs defined within it.
        /// </summary>
        /// <param name="fileName">meta data file name.</param>
        /// <param name="sched">The scheduler.</param>
        public virtual void ProcessFileAndScheduleJobs(string fileName, IScheduler sched)
        {
            ProcessFileAndScheduleJobs(fileName, fileName, sched);
        }

        /// <summary>
        /// Process the xml file in the given location, and schedule all of the
        /// jobs defined within it.
        /// </summary>
        /// <param name="fileName">Name of the file.</param>
        /// <param name="systemId">The system id.</param>
        /// <param name="sched">The sched.</param>
        public virtual void ProcessFileAndScheduleJobs(string fileName, string systemId, IScheduler sched)
        {
            LogicalThreadContext.SetData(ThreadLocalKeyScheduler, sched);
            try
            {
                ProcessFile(fileName, systemId);
                ExecutePreProcessCommands(sched);
                ScheduleJobs(sched);
            }
            finally
            {
                LogicalThreadContext.FreeNamedDataSlot(ThreadLocalKeyScheduler);
            }
        }

        /// <summary>
        /// Schedules the given sets of jobs and triggers.
        /// </summary>
        /// <param name="sched">The sched.</param>
        public virtual void ScheduleJobs(IScheduler sched)
        {
        List<JobDetail> jobs = new List<JobDetail>(loadedJobs);
        List<Trigger> triggers = new List<Trigger>(loadedTriggers);
        
        log.Info("Adding " + jobs.Count + " jobs, " + triggers.Count + " triggers.");
        
        IDictionary<String, List<Trigger>> triggersByFQJobName = BuildTriggersByFQJobNameMap(triggers);
        
        // add each job, and it's associated triggers
            for (int i = 0; i < jobs.Count; i++)
            {
                JobDetail detail = jobs[i];
                // remove jobs as we handle them...
                jobs.RemoveAt(i); 
                i--;

                JobDetail dupeJ = sched.GetJobDetail(detail.Name, detail.Group);

                if ((dupeJ != null))
                {
                    if (!OverWriteExistingData && IgnoreDuplicates)
                    {
                        log.Info("Not overwriting existing job: " + dupeJ.FullName);
                        continue; // just ignore the entry
                    }
                    if (!OverWriteExistingData && !IgnoreDuplicates)
                    {
                        throw new ObjectAlreadyExistsException(detail);
                    }
                }

                if (dupeJ != null)
                {
                    log.Info("Replacing job: " + detail.FullName);
                }
                else
                {
                    log.Info("Adding job: " + detail.FullName);
                }

                List<Trigger> triggersOfJob;
                triggersByFQJobName.TryGetValue(detail.FullName, out triggersOfJob);

                if (!detail.Durable && (triggersOfJob == null || triggersOfJob.Count == 0))
                {
                    if (dupeJ == null)
                    {
                        throw new SchedulerException(
                            "A new job defined without any triggers must be durable: " +
                            detail.FullName);
                    }

                    if ((dupeJ.Durable &&
                         (sched.GetTriggersOfJob(detail.Name, detail.Group).Count == 0)))
                    {
                        throw new SchedulerException(
                            "Can't change existing durable job without triggers to non-durable: " +
                            detail.FullName);
                    }
                }


                if (dupeJ != null || detail.Durable)
                {
                    sched.AddJob(detail, true); // add the job if a replacement or durable
                }
                else
                {
                    bool addJobWithFirstSchedule = true;

                    // Add triggers related to the job...
                    for (int j = 0; j < triggersOfJob.Count; j++)
                    {
                        Trigger trigger = triggersOfJob[j];
                         // remove triggers as we handle them...
                        triggers.RemoveAt(j);
                        j--;

                        bool addedTrigger = false;
                        while (addedTrigger == false)
                        {
                            Trigger dupeT = sched.GetTrigger(trigger.Name, trigger.Group);
                            if (dupeT != null)
                            {
                                if (OverWriteExistingData)
                                {
                                    if (log.IsDebugEnabled)
                                    {
                                        log.Debug(
                                            "Rescheduling job: " + trigger.FullJobName + " with updated trigger: " +
                                            trigger.FullName);
                                    }
                                }
                                else if (IgnoreDuplicates)
                                {
                                    log.Info("Not overwriting existing trigger: " + dupeT.FullName);
                                    continue; // just ignore the trigger (and possibly job)
                                }
                                else
                                {
                                    throw new ObjectAlreadyExistsException(trigger);
                                }

                                if (!dupeT.JobGroup.Equals(trigger.JobGroup) ||
                                    !dupeT.JobName.Equals(trigger.JobName))
                                {
                                    log.WarnFormat("Possibly duplicately named ({0}) triggers in jobs xml file! ",
                                             trigger.FullName);
                                }

                                sched.RescheduleJob(trigger.Name, trigger.Group, trigger);
                            }
                            else
                            {
                                if (log.IsDebugEnabled)
                                {
                                    log.Debug(
                                        "Scheduling job: " + trigger.FullJobName + " with trigger: " +
                                        trigger.FullName);
                                }

                                try
                                {
                                    if (addJobWithFirstSchedule)
                                    {
                                        sched.ScheduleJob(detail, trigger); // add the job if it's not in yet...
                                        addJobWithFirstSchedule = false;
                                    }
                                    else
                                    {
                                        sched.ScheduleJob(trigger);
                                    }
                                }
                                catch (ObjectAlreadyExistsException)
                                {
                                    if (log.IsDebugEnabled)
                                    {
                                        log.Debug("Adding trigger: " + trigger.FullName + " for job: " +
                                            detail.FullName + " failed because the trigger already existed.  " +
                                            "This is likely due to a race condition between multiple instances " +
                                            "in the cluster.  Will try to reschedule instead.");
                                    }
                                    continue;
                                }
                            }
                            addedTrigger = true;
                        }
                    }
                }
            }

            // add triggers that weren't associated with a new job... (those we already handled were removed above)
            foreach (Trigger trigger in triggers) {
            
            
            bool addedTrigger = false;
            while (addedTrigger == false) {
                Trigger dupeT = sched.GetTrigger(trigger.Name, trigger.Group);
                if (dupeT != null) {
                    if(OverWriteExistingData) {
                        if (log.IsDebugEnabled) {
                            log.DebugFormat("Rescheduling job: " + trigger.FullJobName + " with updated trigger: " + trigger.FullName);
                        }
                    }
                    else if(IgnoreDuplicates) {
                        log.Info("Not overwriting existing trigger: " + dupeT.FullName);
                        continue; // just ignore the trigger 
                    }
                    else {
                        throw new ObjectAlreadyExistsException(trigger);
                    }
                    
                    if(!dupeT.JobGroup.Equals(trigger.JobGroup) || !dupeT.JobName.Equals(trigger.JobName)) {
                        log.WarnFormat("Possibly duplicately named ({0}) triggers in jobs xml file! ", trigger.FullName);
                    }
                    
                    sched.RescheduleJob(trigger.Name, trigger.Group, trigger);
                } else {
                    if (log.IsDebugEnabled) {
                        log.Debug(
                            "Scheduling job: " + trigger.FullJobName + " with trigger: " + trigger.FullName);
                    }

                    try {
                        sched.ScheduleJob(trigger);
                    } catch (ObjectAlreadyExistsException) {
                        if (log.IsDebugEnabled) {
                            log.Debug(
                                "Adding trigger: " + trigger.FullName + " for job: " +trigger.FullJobName + 
                                " failed because the trigger already existed.  " +
                                "This is likely due to a race condition between multiple instances " + 
                                "in the cluster.  Will try to reschedule instead.");
                        }
                        continue;
                    }
                }
                addedTrigger = true;
            }
        }

        }

        protected virtual IDictionary<String, List<Trigger>> BuildTriggersByFQJobNameMap(List<Trigger> triggers)
        {
            IDictionary<String, List<Trigger>> triggersByFQJobName = new Dictionary<String, List<Trigger>>();

            foreach (Trigger trigger in triggers)
            {
                List<Trigger> triggersOfJob;
                if (!triggersByFQJobName.TryGetValue(trigger.FullJobName, out triggersOfJob))
                {
                    triggersOfJob = new List<Trigger>();
                    triggersByFQJobName[trigger.FullJobName] = triggersOfJob;
                }
                triggersOfJob.Add(trigger);
            }

            return triggersByFQJobName;
        }

        protected void ExecutePreProcessCommands(IScheduler scheduler)
        {
            foreach (String group in jobGroupsToDelete)
            {
                if (group.Equals("*"))
                {
                    log.Info("Deleting all jobs in ALL groups.");
                    foreach (String groupName in scheduler.JobGroupNames)
                    {
                        if (!jobGroupsToNeverDelete.Contains(groupName))
                        {
                            foreach (String jobName in scheduler.GetJobNames(groupName))
                            {
                                scheduler.DeleteJob(jobName, groupName);
                            }
                        }
                    }
                }
                else
                {
                    if (!jobGroupsToNeverDelete.Contains(group))
                    {
                        log.InfoFormat("Deleting all jobs in group: {}", group);
                        foreach (String jobName in scheduler.GetJobNames(group))
                        {
                            scheduler.DeleteJob(jobName, group);
                        }
                    }
                }
            }

            foreach (String group in triggerGroupsToDelete)
            {
                if (group.Equals("*"))
                {
                    log.Info("Deleting all triggers in ALL groups.");
                    foreach (String groupName in scheduler.TriggerGroupNames)
                    {
                        if (!triggerGroupsToNeverDelete.Contains(groupName))
                        {
                            foreach (String triggerName in scheduler.GetTriggerNames(groupName))
                            {
                                scheduler.UnscheduleJob(triggerName, groupName);
                            }
                        }
                    }
                }
                else
                {
                    if (!triggerGroupsToNeverDelete.Contains(group))
                    {
                        log.InfoFormat("Deleting all triggers in group: {0}", group);
                        foreach (String triggerName in scheduler.GetTriggerNames(group))
                        {
                            scheduler.UnscheduleJob(triggerName, group);
                        }
                    }
                }
            }

            foreach (Key key in jobsToDelete)
            {
                if (!jobGroupsToNeverDelete.Contains(key.Group))
                {
                    log.InfoFormat("Deleting job: {0}", key);
                    scheduler.DeleteJob(key.Name, key.Group);
                }
            }

            foreach (Key key in triggersToDelete)
            {
                if (!triggerGroupsToNeverDelete.Contains(key.Group))
                {
                    log.InfoFormat("Deleting trigger: {0}", key);
                    scheduler.UnscheduleJob(key.Name, key.Group);
                }
            }
        }


        /// <summary>
        /// Adds a detected validation exception.
        /// </summary>
        /// <param name="e">The exception.</param>
        protected virtual void AddValidationException(XmlException e)
        {
            validationExceptions.Add(e);
        }

        /// <summary>
        /// Resets the the number of detected validation exceptions.
        /// </summary>
        protected virtual void ClearValidationExceptions()
        {
            validationExceptions.Clear();
        }

        /// <summary>
        /// Throws a ValidationException if the number of validationExceptions
        /// detected is greater than zero.
        /// </summary>
        /// <exception cref="ValidationException"> 
        /// DTD validation exception.
        /// </exception>
        protected virtual void MaybeThrowValidationException()
        {
            if (validationExceptions.Count > 0)
            {
                throw new ValidationException(validationExceptions);
            }
        }

        public void AddJobGroupToNeverDelete(string jobGroupName)
        {
            jobGroupsToNeverDelete.Add(jobGroupName);
        }

        public void AddTriggerGroupToNeverDelete(string triggerGroupName)
        {
            triggerGroupsToNeverDelete.Add(triggerGroupName);
        }
    }
}