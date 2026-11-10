using Telegram.Td.Api;
using Unigram.Common;
using Unigram.Services;

namespace Unigram.ViewModels.Payments
{
    public class PaymentFormViewModelBase : TLViewModelBase
    {
        public PaymentFormViewModelBase(IProtoService protoService, ICacheService cacheService, ISettingsService settingsService, IEventAggregator aggregator)
            : base(protoService, cacheService, settingsService, aggregator)
        {
        }

        protected Message _message;
        public Message Message
        {
            get
            {
                return _message;
            }
            set
            {
                Set(ref _message, value);
            }
        }

        protected MessageInvoice _invoice;
        public MessageInvoice Invoice
        {
            get
            {
                return _invoice;
            }
            set
            {
                Set(ref _invoice, value);
            }
        }

        public string InvoiceTitle => Invoice?.GetTitle();
        public string InvoiceDescription => Invoice?.GetDescription();

        protected PaymentForm _paymentForm;
        public PaymentForm PaymentForm
        {
            get
            {
                return _paymentForm;
            }
            set
            {
                Set(ref _paymentForm, value);
            }
        }

        public Invoice PaymentInvoice => ModernTdlibCompatibility.GetPaymentFormInvoice(_paymentForm);

        public OrderInfo SavedOrderInfo => ModernTdlibCompatibility.GetPaymentFormSavedOrderInfo(_paymentForm);

        public bool HasSavedCredentials => ModernTdlibCompatibility.HasPaymentFormSavedCredentials(_paymentForm);

        public bool CanSaveCredentials => ModernTdlibCompatibility.GetPaymentFormCanSaveCredentials(_paymentForm);

        public string PaymentFormUrl => ModernTdlibCompatibility.GetPaymentFormUrl(_paymentForm);
    }
}
