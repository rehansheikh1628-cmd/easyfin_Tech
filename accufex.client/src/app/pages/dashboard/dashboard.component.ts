import { Component, OnInit, ChangeDetectorRef } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { StatementService } from '../../services/statement.service';
import { AuthService } from '../../services/auth.service';
import { environment } from '../../../environments/environment';

export interface DashboardRecentStatement {
  id: string;
  originalFileName: string;
  bankName?: string;
  uploadedAt: string;
  status: string;
  processingStatus: number;
  transactionCount: number;
  fileSizeBytes: number;
  fileSizeFormatted: string;
}

export interface DashboardSummary {
  status: string;
  database: string;
  server: string;
  canConnect: boolean;
  totalStatements: number;
  completedStatements: number;
  statementsProcessed: number;
  processingStatements: number;
  failedStatements: number;
  totalTransactions: number;
  clientProfiles: number;
  recentStatements: DashboardRecentStatement[];
}

@Component({
  selector: 'app-dashboard',
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.css'],
  standalone: false
})
export class DashboardComponent implements OnInit {
  stats: DashboardSummary = {
    status: 'Online / Connected',
    database: 'Production',
    server: 'Protected',
    canConnect: true,
    totalStatements: 0,
    completedStatements: 0,
    statementsProcessed: 0,
    processingStatements: 0,
    failedStatements: 0,
    totalTransactions: 0,
    clientProfiles: 1,
    recentStatements: []
  };

  loading = true;
  errorMessage: string | null = null;
  userWorkspaceName = 'Connected Workspace';

  constructor(
    private http: HttpClient,
    public statementService: StatementService,
    private authService: AuthService,
    private cdr: ChangeDetectorRef
  ) {}

  get greeting(): string {
    const hour = new Date().getHours();
    if (hour < 12) return 'Good morning';
    if (hour < 18) return 'Good afternoon';
    return 'Good evening';
  }

  get userDisplayName(): string {
    if (this.authService.currentUser?.fullName) {
      return this.authService.currentUser.fullName;
    }
    if (this.userWorkspaceName && this.userWorkspaceName !== 'Connected Workspace') {
      return this.userWorkspaceName;
    }
    return 'Finance Team';
  }

  ngOnInit(): void {
    this.loadDashboardData();
  }

  loadDashboardData(): void {
    this.loading = true;
    this.errorMessage = null;

    if (this.authService.currentUser?.workspaceName) {
      this.userWorkspaceName = this.authService.currentUser.workspaceName;
    }

    const dashboardUrl = environment.apiUrl && !environment.apiUrl.includes('YOUR-PRODUCTION')
      ? `${environment.apiUrl}/api/dashboard/summary`
      : '/api/dashboard/summary';

    this.http.get<DashboardSummary>(dashboardUrl).subscribe({
      next: (summary) => {
        if (summary) {
          this.stats = {
            ...summary,
            recentStatements: summary.recentStatements || []
          };
        }
        this.loading = false;
        this.cdr.markForCheck();
      },
      error: (err) => {
        console.error('Failed to load dashboard summary:', err);
        this.errorMessage = 'Unable to connect to the server or load dashboard statistics. Please try again.';
        this.loading = false;
        this.cdr.markForCheck();
      }
    });
  }

  downloadStatement(item: DashboardRecentStatement): void {
    this.statementService.downloadStatementFile(item.id, item.originalFileName);
  }
}
