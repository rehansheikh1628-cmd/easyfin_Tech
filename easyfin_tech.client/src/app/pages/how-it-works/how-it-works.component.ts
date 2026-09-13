import { Component, ChangeDetectorRef } from '@angular/core';
import { AuthService } from '../../services/auth.service';

export type WorkflowType = 'bank-to-excel' | 'excel-to-tally' | 'bills-invoices';

@Component({
  selector: 'app-how-it-works',
  templateUrl: './how-it-works.component.html',
  styleUrls: ['./how-it-works.component.css'],
  standalone: false
})
export class HowItWorksComponent {
  activeWorkflow: WorkflowType = 'bank-to-excel';

  constructor(
    public authService: AuthService,
    public cdr: ChangeDetectorRef
  ) {}

  get isAuthenticated(): boolean {
    return this.authService.isAuthenticated;
  }

  setWorkflow(wf: WorkflowType): void {
    this.activeWorkflow = wf;
    this.cdr.markForCheck();
  }
}
