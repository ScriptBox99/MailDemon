﻿using MimeKit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using System.Dynamic;
using System.Diagnostics;

namespace MailDemon
{
    /// <summary>
    /// Handles sending of bulk email
    /// </summary>
    public interface IBulkMailSender
    {
        /// <summary>
        /// Send bulk email
        /// </summary>
        /// <param name="list">List to send email from</param>
        /// <param name="mailCreator">Creates the email message</param>
        /// <param name="mailSender">Sends the email message</param>
        /// <param name="viewBag">View bag</param>
        /// <param name="all">True to send to all subscribers, false to only send to subscribers with a non-empty result (error state)</param>
        /// <param name="fullTemplateName">The template to create, i.e. List@TemplateName</param>
        /// <param name="unsubscribeUrl">The unsubscribe url to put in the message, {0} is the unsubscribe token</param>
        /// <returns>Task</returns>
        Task SendBulkMail(MailList list, IMailCreator mailCreator, IMailSender mailSender, ExpandoObject viewBag, bool all,
            string fullTemplateName, string unsubscribeUrl);
    }

    public class BulkMailSender : IBulkMailSender
    {
        private readonly IServiceProvider serviceProvider;

        // TODO: Use async enumerator
        private IEnumerable<MailToSend> GetMessages(IEnumerable<MailListSubscription> subs, IMailCreator mailCreator, MailList list,
            ExpandoObject viewBag, string fullTemplateName, Action<MailListSubscription, string> callback)
        {
            foreach (MailListSubscription sub in subs)
            {
                MimeMessage message;
                lock (mailCreator)
                {
                    message = mailCreator.CreateMailAsync(fullTemplateName, sub, viewBag, null).Sync();
                }
                message.From.Clear();
                message.To.Clear();
                if (string.IsNullOrWhiteSpace(list.FromEmailName))
                {
                    message.From.Add(new MailboxAddress(list.FromEmailAddress));
                }
                else
                {
                    message.From.Add(new MailboxAddress(list.FromEmailName, list.FromEmailAddress));
                }
                message.To.Add(new MailboxAddress(sub.EmailAddress));
                yield return new MailToSend { Subscription = sub, Message = message, Callback = callback };
            }
        }

        public BulkMailSender(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public async Task SendBulkMail(MailList list, IMailCreator mailCreator, IMailSender mailSender, ExpandoObject viewBag,
            bool all, string fullTemplateName, string unsubscribeUrl)
        {
            MailDemonLog.Warn("Started bulk send for {0}", fullTemplateName);

            DateTime now = DateTime.UtcNow;
            int count = 0;
            List<Task> pendingTasks = new List<Task>();
            Stopwatch timer = Stopwatch.StartNew();

            using (var db = serviceProvider.GetService<MailDemonDatabase>())
            {
                void callbackHandler(MailListSubscription _sub, string error)
                {
                    lock (db)
                    {
                        _sub.Result = error;
                        _sub.ResultTimestamp = DateTime.UtcNow;
                        db.Update(_sub);
                        db.SaveChanges();
                        count++;
                    }
                }

                // use a separate database instance to do the query, that way we can update records in our other database instance
                // preventing locking errors, especially with sqlite drivers
                using (var dbBulk = serviceProvider.GetService<MailDemonDatabase>())
                {
                    IEnumerable<KeyValuePair<string, IEnumerable<MailListSubscription>>> pendingSubs = dbBulk.BeginBulkEmail(list, unsubscribeUrl, all);
                    foreach (KeyValuePair<string, IEnumerable<MailListSubscription>> sub in pendingSubs)
                    {
                        now = DateTime.UtcNow;
                        try
                        {
                            Task task = mailSender.SendMailAsync(sub.Key, GetMessages(sub.Value, mailCreator, list, viewBag, fullTemplateName, callbackHandler));
                            pendingTasks.Add(task);
                        }
                        catch (Exception ex)
                        {
                            MailDemonLog.Error(ex);
                        }
                    }
                }

                await Task.WhenAll(pendingTasks);

                MailDemonLog.Warn("Finished bulk send {0} messages for {1} in {2:0.00} seconds.", count, fullTemplateName, timer.Elapsed.TotalSeconds);
            }
        }
    }
}
