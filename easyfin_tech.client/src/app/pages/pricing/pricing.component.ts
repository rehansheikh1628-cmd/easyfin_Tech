import { Component } from '@angular/core';

@Component({
  selector: 'app-pricing',
  templateUrl: './pricing.component.html',
  styleUrls: ['./pricing.component.css'],
  standalone: false
})
export class PricingComponent {
  billingPeriod: 'monthly' | 'annual' = 'monthly';

  setPeriod(period: 'monthly' | 'annual'): void {
    this.billingPeriod = period;
  }
}
