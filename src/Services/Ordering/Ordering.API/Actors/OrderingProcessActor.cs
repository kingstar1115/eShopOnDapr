﻿namespace Microsoft.eShopOnDapr.Services.Ordering.API.Actors;

public class OrderingProcessActor : Actor, IOrderingProcessActor, IRemindable
{
    private const string OrderDetailsStateName = "OrderDetails";
    private const string OrderStatusStateName = "OrderStatus";

    private const string GracePeriodElapsedReminder = "GracePeriodElapsed";
    private const string StockConfirmedReminder = "StockConfirmed";
    private const string StockRejectedReminder = "StockRejected";
    private const string PaymentSucceededReminder = "PaymentSucceeded";
    private const string PaymentFailedReminder = "PaymentFailed";

    private readonly IEventBus _eventBus;
    private readonly IOptions<OrderingSettings> _settings;

    private int? _preMethodOrderStatusId;

    public OrderingProcessActor(
        ActorHost host,
        IEventBus eventBus,
        IOptions<OrderingSettings> settings)
        : base(host)
    {
        _eventBus = eventBus;
        _settings = settings;
    }

    private Guid OrderId => Guid.Parse(Id.GetId());

    public async Task SubmitAsync(
        string buyerId,
        string buyerEmail,
        string street,
        string city,
        string state,
        string country,
        CustomerBasket basket)
    {
        var orderState = new OrderState
        {
            OrderDate = DateTime.UtcNow,
            OrderStatus = OrderStatus.Submitted,
            Description = "Submitted",
            Address = new OrderAddressState
            {
                Street = street,
                City = city,
                State = state,
                Country = country
            },
            BuyerId = buyerId,
            BuyerEmail = buyerEmail,
            OrderItems = basket.Items
                .Select(item => new OrderItemState
                {
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    UnitPrice = item.UnitPrice,
                    Units = item.Quantity,
                    PictureFileName = item.PictureFileName
                })
                .ToList()
        };

        await StateManager.SetStateAsync(OrderDetailsStateName, orderState);
        await StateManager.SetStateAsync(OrderStatusStateName, OrderStatus.Submitted);

        await RegisterReminderAsync(
            GracePeriodElapsedReminder,
            null,
            TimeSpan.FromSeconds(_settings.Value.GracePeriodTime),
            TimeSpan.FromMilliseconds(-1));

        await _eventBus.PublishAsync(new OrderStatusChangedToSubmittedIntegrationEvent(
            OrderId,
            OrderStatus.Submitted.Name,
            buyerId,
            buyerEmail));
    }

    public async Task NotifyStockConfirmedAsync()
    {
        var statusChanged = await TryUpdateOrderStatusAsync(OrderStatus.AwaitingStockValidation, OrderStatus.Validated);
        if (statusChanged)
        {
            // Simulate a work time by setting a reminder.
            await RegisterReminderAsync(
                StockConfirmedReminder,
                null,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(-1));
        }            
    }

    public async Task NotifyStockRejectedAsync(List<int> rejectedProductIds)
    {
        var statusChanged = await TryUpdateOrderStatusAsync(OrderStatus.AwaitingStockValidation, OrderStatus.Cancelled);
        if (statusChanged)
        {
            var reminderState = JsonSerializer.Serialize(rejectedProductIds);

            // Simulate a work time by setting a reminder.
            await RegisterReminderAsync(
                StockRejectedReminder,
                Encoding.UTF8.GetBytes(reminderState),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(-1));
        }
    }

    public async Task NotifyPaymentSucceededAsync()
    {
        var statusChanged = await TryUpdateOrderStatusAsync(OrderStatus.Validated, OrderStatus.Paid);
        if (statusChanged)
        {
            // Simulate a work time by setting a reminder.
            await RegisterReminderAsync(
                PaymentSucceededReminder,
                null,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(-1));
        }
    }

    public async Task NotifyPaymentFailedAsync()
    {
        var statusChanged = await TryUpdateOrderStatusAsync(OrderStatus.Validated, OrderStatus.Paid);
        if (statusChanged)
        {
            // Simulate a work time by setting a reminder.
            await RegisterReminderAsync(
                PaymentFailedReminder,
                null,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(-1));
        }
    }

    public async Task<bool> CancelAsync()
    {
        var orderStatus = await StateManager.TryGetStateAsync<OrderStatus>(OrderStatusStateName);
        if (!orderStatus.HasValue)
        {
            Logger.LogWarning("Order with Id: {OrderId} cannot be cancelled because it doesn't exist",
                OrderId);

            return false;
        }

        if ( orderStatus.Value.Id == OrderStatus.Paid.Id || orderStatus.Value.Id == OrderStatus.Shipped.Id)
        {
            Logger.LogWarning("Order with Id: {OrderId} cannot be cancelled because it's in status {Status}",
                OrderId, orderStatus.Value.Name);

            return false;
        }

        await StateManager.SetStateAsync(OrderStatusStateName, OrderStatus.Cancelled);

        var order = await StateManager.GetStateAsync<OrderState>(OrderDetailsStateName);

        await _eventBus.PublishAsync(new OrderStatusChangedToCancelledIntegrationEvent(
            OrderId,
            OrderStatus.Cancelled.Name,
            $"The order was cancelled by buyer.",
            order.BuyerId));

        return true;
    }

    public async Task<bool> ShipAsync()
    {
        var statusChanged = await TryUpdateOrderStatusAsync(OrderStatus.Paid, OrderStatus.Shipped);
        if (statusChanged)
        {
            var order = await StateManager.GetStateAsync<OrderState>(OrderDetailsStateName);

            await _eventBus.PublishAsync(new OrderStatusChangedToShippedIntegrationEvent(
                OrderId,
                OrderStatus.Shipped.Name,
                "The order was shipped.",
                order.BuyerId));

            return true;
        }

        return false;
    }

    public Task<OrderState> GetOrderDetails()
    {
        return StateManager.GetStateAsync<OrderState>(OrderDetailsStateName); 
    }

    public Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
    {
        Logger.LogInformation(
            "Received {Actor}[{ActorId}] reminder: {Reminder}",
            nameof(OrderingProcessActor), OrderId, reminderName);

        return reminderName switch
        {
            GracePeriodElapsedReminder => OnGracePeriodElapsedAsync(),
            StockConfirmedReminder => OnStockConfirmedSimulatedWorkDoneAsync(),
            StockRejectedReminder => OnStockRejectedSimulatedWorkDoneAsync(
                JsonSerializer.Deserialize<List<int>>(Encoding.UTF8.GetString(state))!),
            PaymentSucceededReminder => OnPaymentSucceededSimulatedWorkDoneAsync(),
            PaymentFailedReminder => OnPaymentFailedSimulatedWorkDoneAsync(),
            _ => Task.CompletedTask 
        };
    }

    public async Task OnGracePeriodElapsedAsync()
    {
        var statusChanged = await TryUpdateOrderStatusAsync(OrderStatus.Submitted, OrderStatus.AwaitingStockValidation);
        if (statusChanged)
        {
            var order = await StateManager.GetStateAsync<OrderState>(OrderDetailsStateName);

            await _eventBus.PublishAsync(new OrderStatusChangedToAwaitingStockValidationIntegrationEvent(
                OrderId,
                OrderStatus.AwaitingStockValidation.Name,
                "Grace period elapsed; waiting for stock validation.",
                order.OrderItems
                    .Select(orderItem => new OrderStockItem(orderItem.ProductId, orderItem.Units)),
                order.BuyerId));
        }
    }

    public async Task OnStockConfirmedSimulatedWorkDoneAsync()
    {
        var order = await StateManager.GetStateAsync<OrderState>(OrderDetailsStateName);

        await _eventBus.PublishAsync(new OrderStatusChangedToValidatedIntegrationEvent(
            OrderId,
            OrderStatus.Validated.Name,
            "All the items were confirmed with available stock.",
            order.GetTotal(),
            order.BuyerId));
    }

    public async Task OnStockRejectedSimulatedWorkDoneAsync(List<int> rejectedProductIds)
    {
        var order = await StateManager.GetStateAsync<OrderState>(OrderDetailsStateName);

        var rejectedProductNames = order.OrderItems
            .Where(orderItem => rejectedProductIds.Contains(orderItem.ProductId))
            .Select(orderItem => orderItem.ProductName);

        var rejectedDescription = string.Join(", ", rejectedProductNames);

        await _eventBus.PublishAsync(new OrderStatusChangedToCancelledIntegrationEvent(
            OrderId,
            OrderStatus.Cancelled.Name,
            $"The following product items don't have stock: ({rejectedDescription}).",
            order.BuyerId));
    }

    public async Task OnPaymentSucceededSimulatedWorkDoneAsync()
    {
        var order = await StateManager.GetStateAsync<OrderState>(OrderDetailsStateName);

        await _eventBus.PublishAsync(new OrderStatusChangedToPaidIntegrationEvent(
            OrderId,
            OrderStatus.Paid.Name,
            "The payment was performed at a simulated \"American Bank checking bank account ending on XX35071\"",
            order.OrderItems
                .Select(orderItem => new OrderStockItem(orderItem.ProductId, orderItem.Units)),
            order.BuyerId));
    }

    public async Task OnPaymentFailedSimulatedWorkDoneAsync()
    {
        var order = await StateManager.GetStateAsync<OrderState>(OrderDetailsStateName);

        await _eventBus.PublishAsync(new OrderStatusChangedToCancelledIntegrationEvent(
            OrderId,
            OrderStatus.Cancelled.Name,
            "The order was cancelled because payment failed.",
            order.BuyerId));
    }

    protected override async Task OnPreActorMethodAsync(ActorMethodContext actorMethodContext)
    {
        var orderStatus = await StateManager.TryGetStateAsync<OrderStatus>(OrderStatusStateName);

        _preMethodOrderStatusId = orderStatus.HasValue ? orderStatus.Value.Id : (int?)null;
    }

    protected override async Task OnPostActorMethodAsync(ActorMethodContext actorMethodContext)
    {
        var postMethodOrderStatus = await StateManager.GetStateAsync<OrderStatus>(OrderStatusStateName);

        if (_preMethodOrderStatusId != postMethodOrderStatus.Id)
        {
            Logger.LogInformation("Order with Id: {OrderId} has been updated to status {Status}",
                OrderId, postMethodOrderStatus.Name);
        }
    }

    private async Task<bool> TryUpdateOrderStatusAsync(OrderStatus expectedOrderStatus, OrderStatus newOrderStatus)
    {
        var orderStatus = await StateManager.TryGetStateAsync<OrderStatus>(OrderStatusStateName);
        if (!orderStatus.HasValue)
        {
            Logger.LogWarning("Order with Id: {OrderId} cannot be updated because it doesn't exist",
                OrderId);

            return false;
        }

        if (orderStatus.Value.Id != expectedOrderStatus.Id)
        {
            Logger.LogWarning("Order with Id: {OrderId} is in status {Status} instead of expected status {ExpectedStatus}",
                OrderId, orderStatus.Value.Name, expectedOrderStatus.Name);

            return false;
        }

        await StateManager.SetStateAsync(OrderStatusStateName, newOrderStatus);

        return true;
    }
}
