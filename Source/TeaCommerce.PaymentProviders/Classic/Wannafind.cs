﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using TeaCommerce.PaymentProviders.wannafindService;

namespace TeaCommerce.PaymentProviders.Classic {

  [PaymentProvider( "Wannafind" )]
  public class Wannafind : APaymentProvider {

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-wannafind-with-tea-commerce/"; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "shopid" ] = string.Empty;
        defaultSettings[ "lang" ] = "en";
        defaultSettings[ "accepturl" ] = string.Empty;
        defaultSettings[ "declineurl" ] = string.Empty;
        defaultSettings[ "cardtype" ] = string.Empty;
        defaultSettings[ "md5AuthSecret" ] = string.Empty;
        defaultSettings[ "md5CallbackSecret" ] = string.Empty;
        defaultSettings[ "apiUser" ] = string.Empty;
        defaultSettings[ "apiPassword" ] = string.Empty;
        defaultSettings[ "testmode" ] = "1";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "shopid", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = "https://betaling.wannafind.dk/paymentwindow.php",
        Attributes = { { "id", "wannafind" }, { "name", "wannafind" }, { "target", "_self" } }
      };

      string[] settingsToExclude = new[] { "md5AuthSecret", "md5CallbackSecret", "apiUser", "apiPassword", "testmode" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      //orderid
      string orderId = order.CartNumber.Replace( StoreService.Instance.Get( order.StoreId ).OrderSettings.CartNumberPrefix, "" );
      htmlForm.InputFields[ "orderid" ] = orderId;

      //currency
      //Check that the Iso code exists
      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      string currencyStr = Iso4217CurrencyCodes[ currency.IsoCode ];
      htmlForm.InputFields[ "currency" ] = currencyStr;

      //amount
      string amount = ( order.TotalPrice.Value.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );
      htmlForm.InputFields[ "amount" ] = amount;

      htmlForm.InputFields[ "accepturl" ] = teaCommerceContinueUrl;
      htmlForm.InputFields[ "declineurl" ] = teaCommerceCancelUrl;
      htmlForm.InputFields[ "callbackurl" ] = teaCommerceCallBackUrl;

      //authtype
      htmlForm.InputFields[ "authtype" ] = "auth";

      //paytype - legacy
      if ( !settings.ContainsKey( "paytype" ) ) {
        htmlForm.InputFields[ "paytype" ] = "creditcard";
      }

      //cardtype
      string cardType = string.Empty;
      if ( htmlForm.InputFields.ContainsKey( "cardtype" ) ) {
        cardType = htmlForm.InputFields[ "cardtype" ];
        if ( string.IsNullOrEmpty( cardType ) )
          htmlForm.InputFields.Remove( "cardtype" );
      }

      //uniqueorderid
      htmlForm.InputFields[ "uniqueorderid" ] = "true";

      //cardnomask
      htmlForm.InputFields[ "cardnomask" ] = "true";

      //md5securitykey
      if ( settings.ContainsKey( "md5AuthSecret" ) && !string.IsNullOrEmpty( settings[ "md5AuthSecret" ] ) )
        htmlForm.InputFields[ "checkmd5" ] = GenerateMD5Hash( currencyStr + orderId + amount + cardType + settings[ "md5AuthSecret" ] );

      //wannafind dont support to show order line information to the shopper

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "accepturl", "settings" );

      return settings[ "accepturl" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "declineurl", "settings" );

      return settings[ "declineurl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "md5CallbackSecret", "settings" );

        //Write data when testing
        if ( settings.ContainsKey( "testmode" ) && settings[ "testmode" ] == "1" ) {
          LogRequest<Wannafind>( request, logGetData: true );
        }

        string cartNumber = request.QueryString[ "orderid" ];
        string currency = request.QueryString[ "currency" ];
        string amount = request.QueryString[ "amount" ];
        string cardType = settings.ContainsKey( "paytype" ) && settings[ "paytype" ] == "3dsecure" ? TranslateCardTypeToId( request.QueryString[ "cardtype" ] ) : settings.ContainsKey( "cardtype" ) ? settings[ "cardtype" ] : "";

        string md5CheckValue = GenerateMD5Hash( cartNumber + currency + cardType + amount + settings[ "md5CallbackSecret" ] );

        if ( order.CartNumber.Replace( StoreService.Instance.Get( order.StoreId ).OrderSettings.CartNumberPrefix, "" ) == cartNumber && md5CheckValue.Equals( request.QueryString[ "checkmd5callback" ] ) ) {

          string transaction = request.QueryString[ "transacknum" ];
          string cardtype = request.QueryString[ "cardtype" ];
          string cardnomask = request.QueryString[ "cardnomask" ];

          decimal totalAmount = decimal.Parse( amount, CultureInfo.InvariantCulture );

          //Wannafind cant give us info about auto capturing
          callbackInfo = new CallbackInfo( totalAmount / 100M, transaction, PaymentState.Authorized, cardtype, cardnomask );
        } else {
          LoggingService.Instance.Warn<Wannafind>( "Wannafind(" + order.CartNumber + ") - MD5Sum security check failed" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Wannafind>( "Wannafind(" + order.CartNumber + ") - Process callback", exp );
      }

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        try {
          returnArray returnData = GetWannafindServiceClient( settings ).checkTransaction( int.Parse( order.TransactionInformation.TransactionId ), string.Empty, order.CartNumber, string.Empty, string.Empty );

          PaymentState paymentState = PaymentState.Initialized;

          switch ( returnData.returncode ) {
            case 5:
              paymentState = PaymentState.Authorized;
              break;
            case 6:
              paymentState = PaymentState.Captured;
              break;
            case 7:
              paymentState = PaymentState.Cancelled;
              break;
            case 8:
              paymentState = PaymentState.Refunded;
              break;
          }

          apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, paymentState );

        } catch ( WebException ) {
          LoggingService.Instance.Warn<Wannafind>( "Wannafind(" + order.OrderNumber + ") - Error making API request - Wrong credentials or IP address not allowed" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Wannafind>( "Wannafind(" + order.OrderNumber + ") - Get status", exp );
      }

      return apiInfo;
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        try {
          //When capturing of the complete amount - send 0 as parameter for amount
          int returnCode = GetWannafindServiceClient( settings ).captureTransaction( int.Parse( order.TransactionInformation.TransactionId ), 0 );
          if ( returnCode == 0 ) {
            apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Captured );
          } else {
            LoggingService.Instance.Warn<Wannafind>( "Wannafind(" + order.OrderNumber + ") - Error making API request - Error code: " + returnCode );
          }
        } catch ( WebException ) {
          LoggingService.Instance.Warn<Wannafind>( "Wannafind(" + order.OrderNumber + ") - Error making API request - Wrong credentials or IP address not allowed" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Wannafind>( "Wannafind(" + order.OrderNumber + ") - Capture payment", exp );
      }

      return apiInfo;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        try {
          int returnCode = GetWannafindServiceClient( settings ).creditTransaction( int.Parse( order.TransactionInformation.TransactionId ), (int)( order.TransactionInformation.AmountAuthorized.Value * 100M ) );
          if ( returnCode == 0 ) {
            apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Refunded );
          } else {
            LoggingService.Instance.Warn<Wannafind>( "Wannafind(" + order.OrderNumber + ") - Error making API request - Error code: " + returnCode );
          }
        } catch ( WebException ) {
          LoggingService.Instance.Warn<Wannafind>( "Wannafind(" + order.OrderNumber + ") - Error making API request - Wrong credentials or IP address not allowed" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Wannafind>( "Wannafind(" + order.OrderNumber + ") - Refund payment", exp );
      }

      return apiInfo;
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        try {
          int returnCode = GetWannafindServiceClient( settings ).cancelTransaction( int.Parse( order.TransactionInformation.TransactionId ) );
          if ( returnCode == 0 ) {
            apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Cancelled );
          } else {
            LoggingService.Instance.Warn<Wannafind>( "Wannafind(" + order.OrderNumber + ") - Error making API request - Error code: " + returnCode );
          }
        } catch ( WebException ) {
          LoggingService.Instance.Warn<Wannafind>( "Wannafind(" + order.OrderNumber + ") - Error making API request - Wrong credentials or IP address not allowed" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Wannafind>( "Wannafind(" + order.OrderNumber + ") - Cancel payment", exp );
      }

      return apiInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "accepturl":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "declineurl":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "cardtype":
          return settingsKey + "<br/><small>e.g. VISA,MC</small>";
        case "paytype":
          return settingsKey + "<br/><small>creditcard or 3dsecure</small>";
        case "testmode":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    #region Helper methods

    protected pgwapi GetWannafindServiceClient( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "apiUser", "settings" );
      settings.MustContainKey( "apiPassword", "settings" );

      pgwapi paymentGateWayApi = new pgwapi {
        Credentials = new NetworkCredential( settings[ "apiUser" ], settings[ "apiPassword" ] )
      };
      return paymentGateWayApi;
    }

    protected string TranslateCardTypeToId( string cardtype ) {
      switch ( cardtype ) {
        case "DK":
          return "1";
        case "V-DK":
          return "2";
        case "VISA(DK)":
          return "3";
        case "MC(DK)":
          return "4";
        case "MC":
          return "7";
        case "MSC(DK)":
          return "8";
        case "MSC":
          return "9";
        case "DINERS(DA)":
          return "10";
        case "DINERS":
          return "11";
        case "AMEX(DA)":
          return "12";
        case "AMEX":
          return "13";
        case "VISA":
          return "17";
        case "EDK":
          return "6";
        case "N/A":
          return "5";
        case "JCB":
          return "16";
        case "FBF":
          return "18";
      }

      return "";
    }

    #endregion

  }
}
