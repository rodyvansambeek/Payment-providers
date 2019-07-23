using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text;

namespace TeaCommerce.PaymentProviders.Notifications
{
    public class NotificationManager
    {
        internal static void MailError(string emailaddress, string message)
        {
            mailMessage(emailaddress, "[Development]  Platform Error", message);
        }

        private static void mailMessage(string emailaddress, string subject, string message)
        {
            using (SmtpClient client = new SmtpClient())
            {
                MailMessage msg = new MailMessage
                {
                    From = new MailAddress(""),
                    Subject = subject,
                    Body = message
                };
                msg.To.Add(new MailAddress(emailaddress));
                client.Send(msg);
            }
        }
    }
}
