using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Hosting;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using TeaCommerce.Api.Infrastructure.Logging;
using System.Security;
using Mollie.iDEAL;

namespace TeaCommerce.PaymentProviders.Classic
{
    [PaymentProvider("MollieiDeal")]
    public class MollieiDeal : APaymentProvider
    {
        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
                defaultSettings["PartnerId"] = string.Empty;
                defaultSettings["TestMode"] = "1";
                defaultSettings["mollieiDealBanksPropertyAlias"] = "mollieBankList";
                defaultSettings["ReportUrl"] = "http://yourfqdn/base/TC/PaymentCallbackWithoutOrderId/1/MollieiDeal/{PaymentProviderId}";
                defaultSettings["ReturnUrl"] = "http://yourfqdn/shop/Some/Page/On/Return/Customer";
                defaultSettings["CancelUrl"] = "http://yourfqdn/shop/Some/Page/On/Cancel/Customer";
                defaultSettings["ProfileKey"] = string.Empty;
                defaultSettings["MollieUrl"] = "https://secure.mollie.nl/xml/ideal";
                defaultSettings["SecretKey"] = "57CD2F86A82A4F9B96DBD06367519ADA";
                defaultSettings["RoundingMode"] = "0";
                defaultSettings["OrderDescriptionField"] = "Order: ";

                return defaultSettings;
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Api.Models.Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            try
            {
                settings.MustNotBeNull("settings");
                settings.MustContainKey("PartnerId", "settings");
                settings.MustContainKey("ProfileKey", "settings");
                settings.MustContainKey("SecretKey", "settings");
                settings.MustContainKey("ReturnUrl", "settings");
                settings.MustContainKey("mollieiDealBanksPropertyAlias", "settings");
                settings.MustContainKey("OrderDescriptionField", "settings");

                string bankId = order.Properties[settings["mollieiDealBanksPropertyAlias"]];
                string amount = order.TotalPrice.Value.WithVatFormattedWithoutSymbol.Replace(".", "");

                //var hash = GenerateHMACMD5Hash(settings["SecretKey"], string.Format("{0}{1}{2}", settings["PartnerId"], settings["ProfileKey"], order.CartNumber));
                var hash = new HMACSHA256(Encoding.UTF8.GetBytes(settings["SecretKey"])).ComputeHash(Encoding.UTF8.GetBytes(string.Format("{0}{1}{2}", settings["PartnerId"], settings["ProfileKey"], order.CartNumber))).ToBase64();

                var qs = (settings["ReportUrl"].Contains('?')) ? "&" : "?";
                string reporturl = string.Format("{0}{1}cartNumber={2}&hash={3}", settings["ReportUrl"], qs, order.CartNumber, hash);
                string orderDescriptionField = settings["OrderDescriptionField"];
                string description = string.Format("{0} {1}", orderDescriptionField, order.CartNumber);
                qs = (settings["ReturnUrl"].Contains('?')) ? "&" : "?";
                string returnurl = string.Format("{0}{1}orderId={2}", settings["ReturnUrl"], qs, order.Id);
                string profile_key = settings["ProfileKey"];
                string partner_id = settings["PartnerId"];

                Dictionary<string, string> transactionSettings = getTransactionURL(settings, order);
                Dictionary<string, string> inputFields = new Dictionary<string, string>();
                inputFields.Add("paymentUrl", transactionSettings["transactionUrl"]);
                // The next line is here because Mollie does not accept POST requests, only GET requests
                PaymentHtmlForm htmlForm = new PaymentHtmlForm
                {
                    Action = "/pages/redirecttopayment.aspx",
                    InputFields = inputFields
                };
                return htmlForm;
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Log(ex, "GenerateHtmlForm exception");
                throw ex;
            }
        }

        public override string GetCancelUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("CancelUrl", "settings");

            return settings["CancelUrl"];
        }

        public override string GetContinueUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("ReturnUrl", "settings");

            return settings["ReturnUrl"];
        }

        /// <summary>
        /// Gets the cart number.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="settings">The settings.</param>
        /// <returns></returns>
        /// <exception cref="System.Security.SecurityException">Received invalid hash</exception>
        public override string GetCartNumber(HttpRequest request, IDictionary<string, string> settings)
        {
            string cartNumber = "";
            try
            {
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");

                string transaction_id = request["transaction_id"];
                cartNumber = request["cartNumber"];

                var receivedHash = request["hash"];
                // Add plus signs which are lost during url decoding
                if (receivedHash != null)
                    receivedHash = receivedHash.Replace(' ', '+');

                //var calculatedHash = GenerateHMACMD5Hash(settings["SecretKey"], string.Format("{0}{1}{2}", settings["PartnerId"], settings["ProfileKey"], cartNumber));
                var calculatedHash = new HMACSHA256(Encoding.UTF8.GetBytes(settings["SecretKey"])).ComputeHash(Encoding.UTF8.GetBytes(string.Format("{0}{1}{2}", settings["PartnerId"], settings["ProfileKey"], cartNumber))).ToBase64();

                if (receivedHash != calculatedHash)
                {
                    throw new SecurityException(String.Format("Received invalid hash, received hash: {0}, expected hash: {1}", receivedHash, calculatedHash));
                }
                LoggingService.Instance.Log(string.Format("Found cartNumber: {0} for transaction_id {1}", cartNumber, transaction_id));
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Log(exp, "GetCartNumber exception");
            }
            return cartNumber;
        }

        /// <summary>
        /// Processes the callback.
        /// </summary>
        /// <param name="order">The order.</param>
        /// <param name="request">The request.</param>
        /// <param name="settings">The settings.</param>
        /// <returns></returns>
        public override CallbackInfo ProcessCallback(Api.Models.Order order, System.Web.HttpRequest request, IDictionary<string, string> settings)
        {
            CallbackInfo callbackInfo = null;

            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("PartnerId", "settings");
                settings.MustContainKey("RoundingMode", "settings");

                // Call the validation URL to check this order
                IdealCheck idealCheck = new IdealCheck(settings["PartnerId"], settings["TestMode"] == "1", request["transaction_id"]);

                decimal orderAmount = order.TotalPrice.Value.WithVat;
                if (idealCheck.Payed)
                {
                    decimal mollieAmount = idealCheck.Amount;

                    // Check if amount that mollie received is equal to the orders amount
                    if (Math.Round(mollieAmount, 0) == Math.Round(orderAmount, Convert.ToInt32(settings["RoundingMode"])))
                    {
                        callbackInfo = new CallbackInfo(orderAmount, request["transaction_id"], PaymentState.Captured);
                        LoggingService.Instance.Log(string.Format("Mollie: Saved and finalized orderId {0}", order.Id));
                    }
                    else
                    {
                        callbackInfo = new CallbackInfo(orderAmount, request["transaction_id"], PaymentState.Error);
                        LoggingService.Instance.Log(string.Format("Mollie: Controle: MollieAmount:{0} OrderAmount: {1} do not match!", mollieAmount, orderAmount));
                    }
                }
                else
                {
                    LoggingService.Instance.Log(string.Format("Mollie: Controle: iDeal status not payed, for cartId {0}!", order.Id));
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Log(exp, "ProcessCallback exception");
            }

            return callbackInfo;
        }


        /// <summary>
        /// Gets the transaction URL.
        /// </summary>
        /// <param name="settings">The settings.</param>
        /// <param name="order">The order.</param>
        /// <returns></returns>
        public Dictionary<string, string> getTransactionURL(IDictionary<string, string> settings, Api.Models.Order order)
        {
            Dictionary<string, string> resultList = new Dictionary<string, string>();
            settings.MustContainKey("mollieiDealBanksPropertyAlias", "settings");
            settings.MustContainKey("OrderDescriptionField", "settings");
            string bankId = order.Properties[settings["mollieiDealBanksPropertyAlias"]];
            decimal amount = order.TotalPrice.Value.WithVat;

            //var hash = GenerateHMACMD5Hash(settings["SecretKey"], string.Format("{0}{1}{2}", settings["PartnerId"], settings["ProfileKey"], order.CartNumber));
            var hash = new HMACSHA256(Encoding.UTF8.GetBytes(settings["SecretKey"])).ComputeHash(Encoding.UTF8.GetBytes(string.Format("{0}{1}{2}", settings["PartnerId"], settings["ProfileKey"], order.CartNumber))).ToBase64();

            var qs = (settings["ReportUrl"].Contains('?')) ? "&" : "?";
            string reporturl = string.Format("{0}{1}cartNumber={2}&hash={3}", settings["ReportUrl"], qs, order.CartNumber, hash);
            string orderDescriptionField = settings["OrderDescriptionField"];
            string description = string.Format("{0} {1}", orderDescriptionField, order.CartNumber);
            qs = (settings["ReturnUrl"].Contains('?')) ? "&" : "?";
            string returnurl = string.Format("{0}{1}orderId={2}", settings["ReturnUrl"], qs, order.Id);
            string profile_key = settings["ProfileKey"];
            string partner_id = settings["PartnerId"];
            string mollieUrl = settings["MollieUrl"];

            bool testmode = false;
            if (settings["TestMode"] == "1")
            {
                testmode = true;
            }

            IdealFetch idealFetch = new IdealFetch
                (
                    partner_id //replace this by your Mollie partnerid
                    , testmode //testmode
                    , description
                    , reporturl
                    , returnurl
                    , bankId
                    , amount
                );

            resultList.Add("transactionId", idealFetch.TransactionId);
            resultList.Add("transactionUrl", idealFetch.Url);

            return resultList;
        }

        /// <summary>
        /// Get all banks provided by Mollie
        /// </summary>
        /// <param name="settings"></param>
        /// <returns></returns>
        public Dictionary<String, String> getBank(IDictionary<string, string> settings)
        {
            string partnerId = settings["PartnerId"];
            bool testMode = (settings["TestMode"] == "1");

            IdealBanks idealBanks = new IdealBanks(partnerId, testMode);

            Dictionary<string, string> bankenDictionary = new Dictionary<string, string>();
            foreach (Bank bank in idealBanks.Banks)
            {
                bankenDictionary.Add(bank.Name, bank.Id);
            }

            return bankenDictionary;
        }


        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "PartnerId":
                    return settingsKey + "<br/><small>Your Mollie PartnerID</small>";
                case "TestMode":
                    return settingsKey + "<br/><small>Testmode (1 = true, 0 = false)</small>";
                case "ReportUrl":
                    return settingsKey + "<br/><small>The callback page that Mollie will connect to</small>";
                case "ReturnUrl":
                    return settingsKey + "<br/><small>Return URL for customer</small>";
                case "CancelUrl":
                    return settingsKey + "<br/><small>Return URL for customer on cancel</small>";
                case "ProfileKey":
                    return settingsKey + "<br/><small>Mollie ProfileKey</small>";
                case "SecretKey":
                    return settingsKey + "<br/><small>Secret key for hashing messages</small>";
                case "RoundingMode":
                    return settingsKey + "<br/><small>RoundingMode (round result in decimals)</small>";
                case "OrderDescriptionField":
                    return settingsKey + "<br/><small>Order description prefix of payment (as sent to Mollie)</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }
    }
}
