import { Component, OnInit } from '@angular/core';
import { AuthService, UserProfileDto } from '../../services/auth.service';

@Component({
  selector: 'app-settings',
  templateUrl: './settings.component.html',
  styleUrls: ['./settings.component.css'],
  standalone: false
})
export class SettingsComponent implements OnInit {
  activeTab: 'profile' | 'preferences' | 'database' = 'profile';
  currentUser: UserProfileDto | null = null;

  constructor(public authService: AuthService) {}

  ngOnInit(): void {
    this.currentUser = this.authService.currentUser;
    if (!this.currentUser) {
      this.authService.checkAuth().subscribe(u => this.currentUser = u);
    }
  }

  setTab(tab: 'profile' | 'preferences' | 'database'): void {
    this.activeTab = tab;
  }
}
