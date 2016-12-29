﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Classic {

  [PaymentProvider( "Ogone" )]
  public class Ogone : APaymentProvider {

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-ogone-with-tea-commerce/"; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

    public override bool FinalizeAtContinueUrl { get { return true; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "PSPID" ] = string.Empty;
        defaultSettings[ "LANGUAGE" ] = "en_US";
        defaultSettings[ "ACCEPTURL" ] = string.Empty;
        defaultSettings[ "CANCELURL" ] = string.Empty;
        defaultSettings[ "BACKURL" ] = string.Empty;
        defaultSettings[ "PMLIST" ] = string.Empty;
        defaultSettings[ "SHAINPASSPHRASE" ] = string.Empty;
        defaultSettings[ "SHAOUTPASSPHRASE" ] = string.Empty;
        defaultSettings[ "APIUSERID" ] = string.Empty;
        defaultSettings[ "APIPASSWORD" ] = string.Empty;
        defaultSettings[ "TESTMODE" ] = "1";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "SHAINPASSPHRASE", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = GetMethodUrl( "GENERATEFORM", settings )
      };

      string[] settingsToExclude = new[] { "SHAINPASSPHRASE", "SHAOUTPASSPHRASE", "APIUSERID", "APIPASSWORD", "TESTMODE" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) && !string.IsNullOrEmpty( i.Value ) ).ToDictionary( i => i.Key.ToUpperInvariant(), i => i.Value );

      htmlForm.InputFields[ "ORDERID" ] = order.CartNumber;
      htmlForm.InputFields[ "AMOUNT" ] = ( order.TotalPrice.Value.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      //Check that the Iso code exists
      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      htmlForm.InputFields[ "CURRENCY" ] = currency.IsoCode;

      htmlForm.InputFields[ "CN" ] = order.PaymentInformation.FirstName + " " + order.PaymentInformation.LastName;
      htmlForm.InputFields[ "EMAIL" ] = order.PaymentInformation.Email;
      htmlForm.InputFields[ "ACCEPTURL" ] = teaCommerceContinueUrl;
      htmlForm.InputFields[ "DECLINEURL" ] = teaCommerceCancelUrl;
      htmlForm.InputFields[ "EXCEPTIONURL" ] = teaCommerceCancelUrl;
      htmlForm.InputFields[ "CANCELURL" ] = teaCommerceCancelUrl;

      //Ogone dont support to show order line information to the shopper

      string strToHash = string.Join( "", htmlForm.InputFields.OrderBy( i => i.Key ).Select( i => i.Key + "=" + i.Value + settings[ "SHAINPASSPHRASE" ] ) );
      htmlForm.InputFields[ "SHASIGN" ] = new SHA512Managed().ComputeHash( Encoding.UTF8.GetBytes( strToHash ) ).ToHex();

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "ACCEPTURL", "settings" );

      return settings[ "ACCEPTURL" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "CANCELURL", "settings" );

      return settings[ "CANCELURL" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "SHAOUTPASSPHRASE", "settings" );

        //Write data when testing
        if ( settings.ContainsKey( "TESTMODE" ) && settings[ "TESTMODE" ] == "1" ) {
          LogRequest<Ogone>( request, logGetData: true );
        }

        Dictionary<string, string> inputFields = new Dictionary<string, string>();

        string shaSign = request.QueryString[ "SHASIGN" ];
        string strAmount = request.QueryString[ "AMOUNT" ];
        string transaction = request.QueryString[ "PAYID" ];
        string status = request.QueryString[ "STATUS" ];
        string cardType = request.QueryString[ "BRAND" ];
        string cardNo = request.QueryString[ "CARDNO" ];

        foreach ( string key in request.QueryString.Keys ) {
          if ( !key.Equals( "SHASIGN" ) )
            inputFields[ key ] = request.QueryString[ key ];
        }

        string strToHash = string.Join( "", inputFields.OrderBy( i => i.Key ).Select( i => i.Key.ToUpperInvariant() + "=" + i.Value + settings[ "SHAOUTPASSPHRASE" ] ) );
        string digest = new SHA512Managed().ComputeHash( Encoding.UTF8.GetBytes( strToHash ) ).ToHex().ToUpperInvariant();

        if ( order.CartNumber == request.QueryString[ "ORDERID" ] && digest.Equals( shaSign ) ) {
          callbackInfo = new CallbackInfo( decimal.Parse( strAmount, CultureInfo.InvariantCulture ), transaction, status == "5" || status == "51" ? PaymentState.Authorized : PaymentState.Captured, cardType, cardNo );
        } else {
          LoggingService.Instance.Warn<Ogone>( "Ogone(" + order.CartNumber + ") - SHASIGN check isn't valid - Calculated digest: " + digest + " - Ogone SHASIGN: " + shaSign );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Ogone>( "Ogone(" + order.CartNumber + ") - Process callback", exp );
      }

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        XDocument doc = GetStatusInternal( order, settings );
        string status = doc.XPathSelectElement( "//ncresponse" ).Attribute( "STATUS" ).Value;

        PaymentState paymentState = PaymentState.Error;
        switch ( status ) {
          case "5":
          case "51":
            paymentState = PaymentState.Authorized;
            break;
          case "9":
          case "91":
            paymentState = PaymentState.Captured;
            break;
          case "6":
          case "61":
            paymentState = PaymentState.Cancelled;
            break;
          case "7":
          case "71":
          case "8":
          case "81":
            paymentState = PaymentState.Refunded;
            break;
        }

        if ( paymentState != PaymentState.Error ) {
          apiInfo = new ApiInfo( doc.XPathSelectElement( "//ncresponse" ).Attribute( "PAYID" ).Value, paymentState );
        } else {
          LoggingService.Instance.Warn<Ogone>( "Ogone - Error making API request - error code: " + doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERROR" ).Value + " - " + doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERRORPLUS" ).Value );
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Ogone>( "Ogone(" + order.OrderNumber + ") - Get status", exp );
      }

      return apiInfo;
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        XDocument doc = MakeApiRequest( "CAPTURE", "SAS", order, settings );
        string status = doc.XPathSelectElement( "//ncresponse" ).Attribute( "STATUS" ).Value;

        if ( status == "9" || status == "91" ) {
          apiInfo = new ApiInfo( doc.XPathSelectElement( "//ncresponse" ).Attribute( "PAYID" ).Value, PaymentState.Captured );
        } else {
          LoggingService.Instance.Warn<Ogone>( "Ogone - Error making API request - error code: " + doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERROR" ).Value + " - " + doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERRORPLUS" ).Value );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Ogone>( "Ogone(" + order.OrderNumber + ") - Capture payment", exp );
      }

      return apiInfo;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        XDocument statusDoc = GetStatusInternal( order, settings );
        string statusStatus = statusDoc.XPathSelectElement( "//ncresponse" ).Attribute( "STATUS" ).Value;

        if ( statusStatus != "91" ) {
          XDocument doc = MakeApiRequest( "REFUND", "RFS", order, settings );
          string status = doc.XPathSelectElement( "//ncresponse" ).Attribute( "STATUS" ).Value;

          if ( status == "7" || status == "71" || status == "8" || status == "81" ) {
            apiInfo = new ApiInfo( doc.XPathSelectElement( "//ncresponse" ).Attribute( "PAYID" ).Value, PaymentState.Refunded );
          } else {
            LoggingService.Instance.Warn<Ogone>( "Ogone - Error making API request - error code: " + doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERROR" ).Value + " - " + doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERRORPLUS" ).Value );
          }
        } else {
          LoggingService.Instance.Warn<Ogone>( "Ogone - Error making API request - can't refund a transaction with status 91 - please try again in 5 minutes" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Ogone>( "Ogone(" + order.OrderNumber + ") - Refund payment", exp );
      }

      return apiInfo;
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        XDocument doc = MakeApiRequest( "CANCEL", "DES", order, settings );
        string status = doc.XPathSelectElement( "//ncresponse" ).Attribute( "STATUS" ).Value;

        if ( status == "6" || status == "61" ) {
          apiInfo = new ApiInfo( doc.XPathSelectElement( "//ncresponse" ).Attribute( "PAYID" ).Value, PaymentState.Cancelled );
        } else {
          LoggingService.Instance.Warn<Ogone>( "Ogone - Error making API request - error code: " + doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERROR" ).Value + " - " + doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERRORPLUS" ).Value );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Ogone>( "Ogone(" + order.OrderNumber + ") - Cancel payment", exp );
      }

      return apiInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "ACCEPTURL":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "CANCELURL":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "PMLIST":
          return settingsKey + "<br/><small>e.g. VISA,MasterCard</small>";
        case "TESTMODE":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    #region Helper methods

    protected XDocument GetStatusInternal( Order order, IDictionary<string, string> settings ) {
      return MakeApiRequest( "STATUS", string.Empty, order, settings );
    }

    protected XDocument MakeApiRequest( string methodName, string operation, Order order, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "PSPID", "settings" );
      settings.MustContainKey( "APIUSERID", "settings" );
      settings.MustContainKey( "APIPASSWORD", "settings" );
      settings.MustContainKey( "SHAINPASSPHRASE", "settings" );

      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      inputFields[ "PSPID" ] = settings[ "PSPID" ];
      inputFields[ "USERID" ] = settings[ "APIUSERID" ];
      inputFields[ "PSWD" ] = settings[ "APIPASSWORD" ];
      inputFields[ "PAYID" ] = order.TransactionInformation.TransactionId;
      if ( !methodName.Equals( "STATUS" ) ) {
        inputFields[ "AMOUNT" ] = ( order.TransactionInformation.AmountAuthorized.Value * 100M ).ToString( "0", CultureInfo.InvariantCulture );
        inputFields[ "OPERATION" ] = operation;
      }

      string strToHash = string.Join( "", inputFields.OrderBy( i => i.Key ).Select( i => i.Key.ToUpperInvariant() + "=" + i.Value + settings[ "SHAINPASSPHRASE" ] ) );
      inputFields[ "SHASIGN" ] = new SHA512Managed().ComputeHash( Encoding.UTF8.GetBytes( strToHash ) ).ToHex();

      string response = MakePostRequest( GetMethodUrl( methodName, settings ), inputFields );
      return XDocument.Parse( response, LoadOptions.PreserveWhitespace );
    }

    protected string GetMethodUrl( string type, IDictionary<string, string> settings ) {
      string environment = settings.ContainsKey( "TESTMODE" ) && settings[ "TESTMODE" ].Equals( "1" ) ? "test" : "prod";

      switch ( type.ToUpperInvariant() ) {
        case "GENERATEFORM":
          return "https://secure.ogone.com/ncol/" + environment + "/orderstandard_utf8.asp";
        case "STATUS":
          return "https://secure.ogone.com/ncol/" + environment + "/querydirect.asp";
        case "CAPTURE":
        case "CANCEL":
        case "REFUND":
          return "https://secure.ogone.com/ncol/" + environment + "/maintenancedirect.asp";
      }

      return string.Empty;
    }

    #endregion

  }
}
